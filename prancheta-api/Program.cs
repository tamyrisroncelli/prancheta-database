// Prancheta Database API — ponte local entre a página prancheta.html e o Oracle.
//
// Escuta somente em 127.0.0.1 (não aceita conexão de outra máquina).
// A conexão é aberta pela tela de entrada (POST /api/conectar) e fica só na memória deste processo;
// credenciais nunca são devolvidas pela API, gravadas em arquivo ou escritas em log.
//
// A tela (prancheta.html) vai embutida no executável e é servida em http://127.0.0.1:<porta>/;
// ao iniciar, o navegador padrão abre nela.
//
// Opções de linha de comando:
//   --desenv          libera /api/executar (o mesmo que PRANCHETA_AMBIENTE=desenv)
//   --porta=5090      porta HTTP (o mesmo que PRANCHETA_PORTA; padrão 5080)
//   --sem-navegador   não abre o navegador ao iniciar
//
// Variáveis de ambiente:
//   PRANCHETA_ORACLE   opcional: connection string ODP.NET para já iniciar conectado
//   PRANCHETA_AMBIENTE "desenv" libera /api/executar; qualquer outro valor deixa a API somente leitura
//   PRANCHETA_PORTA    porta HTTP (padrão 5080)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Dapper;
using Oracle.ManagedDataAccess.Client;

bool Opcao(string nome) => args.Any(a => string.Equals(a, nome, StringComparison.OrdinalIgnoreCase));
string? Valor(string nome) => args.Select(a => a.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && string.Equals(x[0], nome, StringComparison.OrdinalIgnoreCase))?[1];
// Argumentos próprios ficam fora do parser do ASP.NET (que não conhece --desenv etc.).
var argsHost = args.Where(a => !a.StartsWith("--desenv", StringComparison.OrdinalIgnoreCase)
    && !a.StartsWith("--porta", StringComparison.OrdinalIgnoreCase)
    && !a.StartsWith("--sem-navegador", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(argsHost);

var cfg = new PranchetaConfig(
    connectionString: Environment.GetEnvironmentVariable("PRANCHETA_ORACLE") ?? "",
    ambiente: Opcao("--desenv") ? "desenv"
        : (Environment.GetEnvironmentVariable("PRANCHETA_AMBIENTE") ?? builder.Configuration["Prancheta:Ambiente"] ?? "").Trim().ToLowerInvariant(),
    porta: int.TryParse(Valor("--porta") ?? Environment.GetEnvironmentVariable("PRANCHETA_PORTA"), out var p) && p is > 0 and < 65536 ? p : 5080);
var abrirNavegador = !Opcao("--sem-navegador");
var urlTela = $"http://127.0.0.1:{cfg.Porta}/";

// Instância única: se a porta já está em uso por outra Prancheta, só abre a tela dela e sai.
if (PortaOcupada(cfg.Porta))
{
    Console.WriteLine($"A porta {cfg.Porta} já está em uso. Se a Prancheta Database já está aberta, use {urlTela}");
    Console.WriteLine("Para outra porta: PranchetaDatabase.exe --porta=5090");
    if (abrirNavegador) AbrirNavegador(urlTela);
    return;
}

builder.WebHost.UseUrls($"http://127.0.0.1:{cfg.Porta}");
builder.Services.AddSingleton(cfg);
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(pol => pol
    // A página costuma ser aberta direto do disco (file://), que envia Origin "null".
    .SetIsOriginAllowed(origin => origin == "null" || IsLocalOrigin(origin))
    .AllowAnyHeader()
    .WithMethods("GET", "POST")));

var app = builder.Build();

// Erros conhecidos viram ProblemDetails com mensagem legível para a tela;
// o resto fica como 500 genérico (detalhe só no console da API).
app.UseExceptionHandler(erro => erro.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (status, titulo, detalhe) = ex switch
    {
        NaoConectadoException => (409, "Não conectado ao Oracle.", "Conecte pela tela de entrada."),
        OracleException o => (502, "Erro do Oracle", Ora(o)),
        _ => (500, "Erro interno da API.", "Veja o console da prancheta-api.")
    };
    if (status == 500) app.Logger.LogError(ex, "Erro não tratado em {Rota}", ctx.Request.Path);
    ctx.Response.StatusCode = status;
    await Results.Problem(statusCode: status, title: titulo, detail: detalhe).ExecuteAsync(ctx);
}));

// Chrome pede este cabeçalho no preflight quando uma página chama um endereço local.
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsOptions(ctx.Request.Method))
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    await next();
});
app.UseCors();

var logPath = Path.Combine(AppContext.BaseDirectory, "execucoes.log");
const int MaxComandos = 500;

if (string.IsNullOrWhiteSpace(cfg.ConnectionString))
    app.Logger.LogInformation("Aguardando conexão pela tela de entrada da Prancheta Database.");
app.Logger.LogInformation("Ambiente: {Ambiente}. Execução de DDL {Estado}.",
    cfg.Ambiente == "" ? "(não informado)" : cfg.Ambiente, cfg.PodeExecutar ? "LIBERADA" : "BLOQUEADA (somente leitura)");

// Tela: a página embutida no executável. A meta "prancheta-api" avisa a página que a API é a própria origem.
var tela = CarregarTela();
app.MapGet("/", () => tela is null
    ? Results.Problem(statusCode: 500, title: "Tela não embutida", detail: "Recompile o projeto com prancheta.html ao lado da pasta prancheta-api.")
    : Results.Content(tela, "text/html; charset=utf-8"));

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Prancheta Database em {Url} — feche esta janela para encerrar.", urlTela);
    if (abrirNavegador) AbrirNavegador(urlTela);
});

var api = app.MapGroup("/api");

// Saúde: diz se a conexão funciona, com qual usuário e se a execução está liberada.
api.MapGet("/saude", async (PranchetaConfig c) =>
{
    if (string.IsNullOrWhiteSpace(c.ConnectionString))
        return Results.Ok(new { conectado = false, ambiente = c.Ambiente, podeExecutar = false, erro = "Não conectado ao Oracle." });
    try
    {
        await using var cn = await Abrir(c);
        var info = await cn.QuerySingleAsync<(string Usuario, string Banco)>(
            "SELECT USER, SYS_CONTEXT('USERENV','DB_NAME') FROM dual");
        return Results.Ok(new { conectado = true, ambiente = c.Ambiente, podeExecutar = c.PodeExecutar, usuario = info.Usuario, banco = info.Banco, versao = cn.ServerVersion });
    }
    catch (OracleException ex)
    {
        return Results.Ok(new { conectado = false, ambiente = c.Ambiente, podeExecutar = false, erro = Ora(ex) });
    }
});

// Conectar: valida usuário/senha/servidor abrindo uma conexão; se abrir, guarda só na memória do processo.
api.MapPost("/conectar", async (PranchetaConfig c, PedidoConexao pedido) =>
{
    if (string.IsNullOrWhiteSpace(pedido.Usuario) || string.IsNullOrEmpty(pedido.Senha) || string.IsNullOrWhiteSpace(pedido.Servidor))
        return Results.Problem(statusCode: 400, title: "Dados incompletos", detail: "Informe servidor, usuário e senha.");
    // O builder escapa os valores: um ';' na senha não injeta parâmetro na connection string.
    var csb = new OracleConnectionStringBuilder
    {
        UserID = pedido.Usuario.Trim(), Password = pedido.Senha, DataSource = pedido.Servidor.Trim(), ConnectionTimeout = 15
    };
    try
    {
        await using var cn = new OracleConnection(csb.ConnectionString);
        await cn.OpenAsync();
    }
    catch (OracleException ex)
    {
        return Results.Problem(statusCode: 401, title: "Falha ao conectar", detail: Ora(ex));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.Problem(statusCode: 400, title: "Servidor inválido", detail: "Use o formato host:porta/serviço.");
    }
    c.ConnectionString = csb.ConnectionString;
    return Results.Ok(new { conectado = true });
});

// Desconectar: esquece a conexão guardada e fecha o pool.
api.MapPost("/desconectar", (PranchetaConfig c) =>
{
    c.ConnectionString = "";
    OracleConnection.ClearAllPools();
    return Results.Ok(new { conectado = false });
});

// Catálogo: mesmo formato que a página usa em st.cat.tables.
api.MapGet("/catalogo", async (PranchetaConfig c, string? owner, string? filtro) =>
{
    await using var cn = await Abrir(c);
    owner = string.IsNullOrWhiteSpace(owner) ? await cn.ExecuteScalarAsync<string>("SELECT USER FROM dual") : owner.Trim().ToUpperInvariant();
    var like = string.IsNullOrWhiteSpace(filtro) ? "%" : filtro.Trim().ToUpperInvariant().Replace('*', '%');
    const string sql = """
        WITH pk AS (
          SELECT cc.table_name, cc.column_name
            FROM all_constraints k
            JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
           WHERE k.owner = :owner AND k.constraint_type = 'P'),
        fk AS (
          SELECT fc.table_name, fc.column_name, MIN(r.table_name || '.' || rc.column_name) AS ref
            FROM all_constraints f
            JOIN all_cons_columns fc ON fc.owner = f.owner AND fc.constraint_name = f.constraint_name
            JOIN all_constraints r ON r.owner = f.r_owner AND r.constraint_name = f.r_constraint_name
            JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = fc.position
           WHERE f.owner = :owner AND f.constraint_type = 'R'
           GROUP BY fc.table_name, fc.column_name)
        SELECT c.table_name AS Tabela, c.column_name AS Coluna, c.data_type AS Tipo,
               c.char_length AS Tam, c.data_precision AS Prec, c.data_scale AS Escala,
               c.nullable AS Nulo, CASE WHEN pk.column_name IS NOT NULL THEN 'S' END AS Pk, fk.ref AS Fk
          FROM all_tab_columns c
          JOIN all_tables t ON t.owner = c.owner AND t.table_name = c.table_name
          LEFT JOIN pk ON pk.table_name = c.table_name AND pk.column_name = c.column_name
          LEFT JOIN fk ON fk.table_name = c.table_name AND fk.column_name = c.column_name
         WHERE c.owner = :owner AND c.table_name LIKE :filtro AND c.table_name NOT LIKE 'BIN$%'
         ORDER BY c.table_name, c.column_id
        """;
    var rows = await cn.QueryAsync<ColunaCatalogo>(new CommandDefinition(sql, new { owner, filtro = like }, commandTimeout: 120));
    var tables = new SortedDictionary<string, object>();
    foreach (var g in rows.GroupBy(r => r.Tabela))
        tables[g.Key] = new
        {
            cols = g.ToDictionary(r => r.Coluna, r => new
            {
                type = r.Tipo, len = r.Tam ?? 0, prec = r.Prec ?? 0, scale = r.Escala ?? 0,
                nullable = r.Nulo != "N", pk = r.Pk == "S", fk = r.Fk ?? ""
            })
        };
    return Results.Ok(new { owner, filtro = like, geradoEm = DateTime.Now, tabelas = tables.Count, tables });
});

// Pesquisa de tabelas pelo nome (parte do nome), em todos os owners que o usuário enxerga.
// Usada para escolher uma tabela existente como base de uma nova transação.
api.MapGet("/tabelas", async (PranchetaConfig c, string? nome, string? owner) =>
{
    var termo = (nome ?? "").Trim().ToUpperInvariant().Replace('*', '%');
    if (termo.Replace("%", "").Length < 2)
        return Results.Problem(title: "Informe ao menos 2 letras do nome da tabela.", statusCode: 400);
    if (!termo.Contains('%')) termo = $"%{termo}%";
    var dono = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim().ToUpperInvariant();
    await using var cn = await Abrir(c);
    const string sql = """
        SELECT * FROM (
          SELECT t.owner AS Owner, t.table_name AS Tabela, t.num_rows AS Linhas, tc.comments AS Comentario
            FROM all_tables t
            LEFT JOIN all_tab_comments tc ON tc.owner = t.owner AND tc.table_name = t.table_name
           WHERE t.table_name LIKE :termo
             AND (:dono IS NULL OR t.owner = :dono)
             AND t.table_name NOT LIKE 'BIN$%'
             AND t.owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','CTXSYS','ORDSYS','OLAPSYS','WMSYS','DBSNMP','APEX_PUBLIC_USER','LBACSYS','DVSYS','GSMADMIN_INTERNAL','AUDSYS','OJVMSYS')
           ORDER BY CASE WHEN t.owner = USER THEN 0 ELSE 1 END, t.owner, t.table_name)
         WHERE ROWNUM <= 200
        """;
    var lista = (await cn.QueryAsync<TabelaEncontrada>(new CommandDefinition(sql, new { termo, dono }, commandTimeout: 60))).ToList();
    return Results.Ok(new { termo, total = lista.Count, limite = 200, tabelas = lista });
});

// Estrutura de uma tabela: colunas, tipos, PK, FK e comentários.
api.MapGet("/tabela", async (PranchetaConfig c, string? owner, string? nome) =>
{
    if (string.IsNullOrWhiteSpace(nome))
        return Results.Problem(title: "Informe o nome da tabela.", statusCode: 400);
    await using var cn = await Abrir(c);
    var dono = await Owner(cn, owner);
    var tabela = nome.Trim().ToUpperInvariant();
    const string sql = """
        WITH pk AS (
          SELECT cc.column_name
            FROM all_constraints k
            JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
           WHERE k.owner = :dono AND k.table_name = :tabela AND k.constraint_type = 'P'),
        fk AS (
          SELECT fc.column_name, MIN(r.owner || '.' || r.table_name || '.' || rc.column_name) AS ref
            FROM all_constraints f
            JOIN all_cons_columns fc ON fc.owner = f.owner AND fc.constraint_name = f.constraint_name
            JOIN all_constraints r ON r.owner = f.r_owner AND r.constraint_name = f.r_constraint_name
            JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = fc.position
           WHERE f.owner = :dono AND f.table_name = :tabela AND f.constraint_type = 'R'
           GROUP BY fc.column_name)
        SELECT c.column_name AS Coluna, c.data_type AS Tipo, c.char_length AS Tam, c.data_precision AS Prec,
               c.data_scale AS Escala, c.nullable AS Nulo, CASE WHEN pk.column_name IS NOT NULL THEN 'S' END AS Pk,
               fk.ref AS Fk, cc.comments AS Comentario
          FROM all_tab_columns c
          LEFT JOIN pk ON pk.column_name = c.column_name
          LEFT JOIN fk ON fk.column_name = c.column_name
          LEFT JOIN all_col_comments cc ON cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name
         WHERE c.owner = :dono AND c.table_name = :tabela
         ORDER BY c.column_id
        """;
    var cols = (await cn.QueryAsync<ColunaCatalogo>(new CommandDefinition(sql, new { dono, tabela }, commandTimeout: 60))).ToList();
    if (cols.Count == 0)
        return Results.Problem(title: $"Tabela {dono}.{tabela} não encontrada ou sem acesso.", statusCode: 404);
    var comentario = await cn.ExecuteScalarAsync<string?>(
        "SELECT comments FROM all_tab_comments WHERE owner = :dono AND table_name = :tabela", new { dono, tabela });
    return Results.Ok(new
    {
        owner = dono, tabela, comentario,
        colunas = cols.Select(r => new
        {
            nome = r.Coluna, type = r.Tipo, len = r.Tam ?? 0, prec = r.Prec ?? 0, scale = r.Escala ?? 0,
            nullable = r.Nulo != "N", pk = r.Pk == "S", fk = r.Fk ?? "", comentario = r.Comentario ?? ""
        })
    });
});

// Análise (dry-run): o DDL do Oracle faz commit implícito e não tem simulação real.
// Por isso a análise consulta o dicionário e diz, comando a comando, o que vai acontecer.
api.MapPost("/analisar", async (PranchetaConfig c, PedidoScript pedido) =>
{
    if ((pedido.Comandos?.Count ?? 0) > MaxComandos)
        return Results.Problem(statusCode: 400, title: "Script grande demais", detail: $"Limite de {MaxComandos} comandos por conferência.");
    await using var cn = await Abrir(c);
    var owner = await Owner(cn, pedido.Owner);
    var itens = new List<object>();
    var ordem = 0;
    // Objetos criados por comandos anteriores do mesmo script contam como existentes nos comandos seguintes.
    var criados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    async Task<bool> Ha(string o, string n) => criados.Contains($"{o}.{n}") || await Existe(cn, o, n);
    foreach (var cmd in pedido.Comandos ?? [])
    {
        ordem++;
        var (tipo, objeto, tabela, coluna) = Classificar(cmd.Sql);
        string situacao = "ok", aviso = "";
        var permitido = Permitido(cmd.Sql, pedido.PermitirDrop);
        if (!permitido.Ok) { situacao = "bloqueado"; aviso = permitido.Motivo; }
        else
        {
            var (own, nome) = Qualificado(objeto, owner);
            switch (tipo)
            {
                case "CREATE TABLE": case "CREATE SEQUENCE": case "CREATE INDEX": case "CREATE UNIQUE INDEX":
                    if (await Ha(own, nome)) { situacao = "falha"; aviso = $"{nome} já existe em {own}: o comando vai falhar."; }
                    else { situacao = "novo"; criados.Add($"{own}.{nome}"); }
                    break;
                case "CREATE OR REPLACE VIEW": case "CREATE OR REPLACE TRIGGER":
                    situacao = await Ha(own, nome) ? "substitui" : "novo";
                    criados.Add($"{own}.{nome}");
                    if (situacao == "substitui") aviso = $"{nome} já existe e será substituído.";
                    break;
                case "ALTER TABLE ADD COLUMN":
                    var (tOwn, tNome) = Qualificado(tabela, owner);
                    if (!await Ha(tOwn, tNome)) { situacao = "falha"; aviso = $"Tabela {tNome} não existe em {tOwn}."; }
                    else if (coluna != null && !criados.Contains($"{tOwn}.{tNome}") && await ColunaExiste(cn, tOwn, tNome, coluna)) { situacao = "falha"; aviso = $"Coluna {coluna} já existe em {tNome}."; }
                    else if (Regex.IsMatch(cmd.Sql, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(cmd.Sql, @"\bDEFAULT\b", RegexOptions.IgnoreCase)
                             && !criados.Contains($"{tOwn}.{tNome}") && await TemLinhas(cn, tOwn, tNome)) { situacao = "falha"; aviso = $"{tNome} tem dados: coluna NOT NULL sem DEFAULT vai falhar (ORA-01758)."; }
                    else situacao = "altera";
                    break;
                case "ALTER TABLE ADD CONSTRAINT":
                    var (cOwn, cTab) = Qualificado(tabela, owner);
                    if (!await Ha(cOwn, cTab)) { situacao = "falha"; aviso = $"Tabela {cTab} não existe em {cOwn}."; }
                    else if (await ConstraintExiste(cn, cOwn, objeto)) { situacao = "falha"; aviso = $"Constraint {objeto} já existe."; }
                    else situacao = "altera";
                    break;
                case "DROP":
                    situacao = await Existe(cn, own, nome) ? "remove" : "falha";
                    aviso = situacao == "remove" ? $"{nome} será removido (irreversível)." : $"{nome} não existe.";
                    break;
            }
        }
        itens.Add(new { ordem, tipo, objeto, situacao, aviso });
    }
    return Results.Ok(new { owner, ambiente = c.Ambiente, podeExecutar = c.PodeExecutar, itens });
});

// Execução: só com PRANCHETA_AMBIENTE=desenv, só DDL da lista permitida, e com a palavra de confirmação digitada na tela.
api.MapPost("/executar", async (PranchetaConfig c, PedidoScript pedido, ILogger<PranchetaConfig> log) =>
{
    if (!c.PodeExecutar)
        return Results.Problem(statusCode: 403, title: "Execução bloqueada",
            detail: "A API só executa quando PRANCHETA_AMBIENTE=desenv. Com outro valor ela é somente leitura.");
    if (pedido.Confirmacao != "EXECUTAR")
        return Results.Problem(statusCode: 400, title: "Confirmação ausente", detail: "Digite EXECUTAR na tela para confirmar.");
    var comandos = pedido.Comandos ?? [];
    if (comandos.Count == 0)
        return Results.Problem(statusCode: 400, title: "Script vazio", detail: "Nenhum comando para executar.");
    if (comandos.Count > MaxComandos)
        return Results.Problem(statusCode: 400, title: "Script grande demais", detail: $"Limite de {MaxComandos} comandos por execução.");
    var bloqueado = comandos.Select((x, i) => (i, Permitido(x.Sql, pedido.PermitirDrop))).FirstOrDefault(x => !x.Item2.Ok);
    if (bloqueado.Item2.Motivo is { Length: > 0 } motivo)
        return Results.Problem(statusCode: 400, title: "Comando não permitido", detail: $"Comando {bloqueado.i + 1}: {motivo}");

    await using var cn = await Abrir(c);
    var usuario = await cn.ExecuteScalarAsync<string>("SELECT USER FROM dual");
    var resultados = new List<ResultadoComando>();
    var ordem = 0;
    foreach (var cmd in comandos)
    {
        ordem++;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var oc = cn.CreateCommand();
            oc.CommandText = TextoExecutavel(cmd);
            oc.CommandTimeout = 300;
            await oc.ExecuteNonQueryAsync();
            // Trigger e view compilam "com erro" sem lançar exceção: consulta all_errors.
            var aviso = await ErrosCompilacao(cn, cmd.Sql);
            resultados.Add(new ResultadoComando(ordem, aviso == null, sw.ElapsedMilliseconds, aviso));
            Registrar(logPath, usuario, cmd.Sql, aviso == null ? "OK" : "COMPILOU COM ERRO: " + aviso);
            if (aviso != null) break;
        }
        catch (OracleException ex)
        {
            var erro = Ora(ex);
            resultados.Add(new ResultadoComando(ordem, false, sw.ElapsedMilliseconds, erro));
            Registrar(logPath, usuario, cmd.Sql, erro);
            log.LogWarning("Comando {Ordem} falhou: {Erro}", ordem, erro);
            break; // para no primeiro erro; o que já rodou fica aplicado (DDL faz commit implícito)
        }
    }
    var aplicados = resultados.Count(r => r.Ok);
    return Results.Ok(new { usuario, total = comandos.Count, aplicados, interrompido = aplicados < comandos.Count, resultados });
});

app.Run();

// ───────── apoio ─────────

static string? CarregarTela()
{
    using var s = typeof(PranchetaConfig).Assembly.GetManifestResourceStream("prancheta.html");
    if (s is null) return null;
    using var r = new StreamReader(s);
    var html = r.ReadToEnd();
    var marca = "<meta name=\"prancheta-api\" content=\"mesma-origem\">";
    var i = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
    return i < 0 ? marca + html : html.Insert(i, marca + "\n");
}

static bool PortaOcupada(int porta)
{
    try
    {
        using var l = new TcpListener(IPAddress.Loopback, porta);
        l.Start();
        l.Stop();
        return false;
    }
    catch (SocketException) { return true; }
}

static void AbrirNavegador(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception) { Console.WriteLine($"Abra no navegador: {url}"); }
}

static bool IsLocalOrigin(string origin) =>
    Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host is "localhost" or "127.0.0.1" or "[::1]");

static async Task<OracleConnection> Abrir(PranchetaConfig c)
{
    if (string.IsNullOrWhiteSpace(c.ConnectionString))
        throw new NaoConectadoException();
    var cn = new OracleConnection(c.ConnectionString);
    await cn.OpenAsync();
    return cn;
}

static async Task<string> Owner(OracleConnection cn, string? owner) =>
    string.IsNullOrWhiteSpace(owner) ? (await cn.ExecuteScalarAsync<string>("SELECT USER FROM dual"))! : owner.Trim().ToUpperInvariant();

// SQL comum não aceita ';' no fim via ODP.NET; bloco PL/SQL (trigger) precisa do ';' de "END;"
// e não pode levar a barra '/' que o SQL*Plus usa como terminador.
static string TextoExecutavel(ComandoSql cmd)
{
    var s = (cmd.Sql ?? "").Trim();
    if (cmd.Plsql || Regex.IsMatch(s, @"^CREATE\s+(OR\s+REPLACE\s+)?TRIGGER\b", RegexOptions.IgnoreCase))
        return Regex.Replace(s, @"\s*/\s*$", "");
    return s.TrimEnd(';').TrimEnd();
}

static string PrimeiraLinha(string s) => s.Split('\n')[0].Trim();

// Mensagem curta do Oracle; algumas já vêm com o prefixo ORA-, outras não.
static string Ora(OracleException ex)
{
    var m = PrimeiraLinha(ex.Message);
    return m.StartsWith("ORA-") ? m : $"ORA-{ex.Number:00000}: {m}";
}

static (string Owner, string Nome) Qualificado(string? objeto, string owner)
{
    if (string.IsNullOrEmpty(objeto)) return (owner, "");
    var p = objeto.Split('.');
    return p.Length == 2 ? (p[0], p[1]) : (owner, p[0]);
}

// Lista permitida: só DDL de estrutura. DML, GRANT, TRUNCATE, PL/SQL anônimo etc. são recusados.
static (bool Ok, string Motivo) Permitido(string sql, bool permitirDrop)
{
    var s = Regex.Replace(sql ?? "", @"^\s*(--[^\n]*\n\s*)*", "").TrimStart().ToUpperInvariant();
    if (s.Length == 0) return (false, "Comando vazio.");
    if (Regex.IsMatch(s, @"^(CREATE\s+(OR\s+REPLACE\s+)?(TABLE|SEQUENCE|(UNIQUE\s+)?INDEX|VIEW|TRIGGER)\b|ALTER\s+TABLE\b|COMMENT\s+ON\b)"))
    {
        if (Regex.IsMatch(s, @"^ALTER\s+TABLE\s+\S+\s+(DROP|RENAME|MOVE)\b") && !permitirDrop)
            return (false, "ALTER TABLE ... DROP/RENAME só com a opção de remoção marcada.");
        return (true, "");
    }
    if (Regex.IsMatch(s, @"^DROP\s+(TABLE|SEQUENCE|INDEX|VIEW|TRIGGER)\b"))
        return permitirDrop ? (true, "") : (false, "DROP só com a opção de remoção marcada (rollback).");
    return (false, "Só são aceitos CREATE/ALTER TABLE/COMMENT/DROP de estrutura.");
}

static (string Tipo, string? Objeto, string? Tabela, string? Coluna) Classificar(string sql)
{
    var s = Regex.Replace(sql ?? "", @"^\s*(--[^\n]*\n\s*)*", "").Trim();
    Match m;
    if ((m = Regex.Match(s, @"^CREATE\s+TABLE\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE TABLE", N(m), null, null);
    if ((m = Regex.Match(s, @"^CREATE\s+SEQUENCE\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE SEQUENCE", N(m), null, null);
    if ((m = Regex.Match(s, @"^CREATE\s+UNIQUE\s+INDEX\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE UNIQUE INDEX", N(m), null, null);
    if ((m = Regex.Match(s, @"^CREATE\s+INDEX\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE INDEX", N(m), null, null);
    if ((m = Regex.Match(s, @"^CREATE\s+OR\s+REPLACE\s+VIEW\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE OR REPLACE VIEW", N(m), null, null);
    if ((m = Regex.Match(s, @"^CREATE\s+OR\s+REPLACE\s+TRIGGER\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("CREATE OR REPLACE TRIGGER", N(m), null, null);
    if ((m = Regex.Match(s, @"^ALTER\s+TABLE\s+([\w$#.""]+)\s+ADD\s+CONSTRAINT\s+([\w$#""]+)", RegexOptions.IgnoreCase)).Success)
        return ("ALTER TABLE ADD CONSTRAINT", m.Groups[2].Value.Trim('"').ToUpperInvariant(), N(m), null);
    if ((m = Regex.Match(s, @"^ALTER\s+TABLE\s+([\w$#.""]+)\s+ADD\s*\(\s*([\w$#""]+)", RegexOptions.IgnoreCase)).Success)
        return ("ALTER TABLE ADD COLUMN", N(m), N(m), m.Groups[2].Value.Trim('"').ToUpperInvariant());
    if ((m = Regex.Match(s, @"^ALTER\s+TABLE\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("ALTER TABLE", N(m), N(m), null);
    if ((m = Regex.Match(s, @"^COMMENT\s+ON\s+(TABLE|COLUMN)\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("COMMENT", m.Groups[2].Value.ToUpperInvariant(), null, null);
    if ((m = Regex.Match(s, @"^DROP\s+\w+\s+([\w$#.""]+)", RegexOptions.IgnoreCase)).Success) return ("DROP", N(m), null, null);
    return ("OUTRO", null, null, null);
    static string N(Match m) => m.Groups[1].Value.Replace("\"", "").ToUpperInvariant();
}

static async Task<bool> Existe(OracleConnection cn, string owner, string nome) =>
    await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM all_objects WHERE owner = :owner AND object_name = :nome", new { owner, nome }) > 0;

static async Task<bool> ColunaExiste(OracleConnection cn, string owner, string tabela, string coluna) =>
    await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM all_tab_columns WHERE owner = :owner AND table_name = :tabela AND column_name = :coluna", new { owner, tabela, coluna }) > 0;

static async Task<bool> ConstraintExiste(OracleConnection cn, string owner, string? nome) =>
    nome != null && await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM all_constraints WHERE owner = :owner AND constraint_name = :nome", new { owner, nome }) > 0;

static async Task<bool> TemLinhas(OracleConnection cn, string owner, string tabela)
{
    // Nome já validado contra o dicionário (Existe) antes de montar o SQL.
    var nome = $"\"{owner.Replace("\"", "")}\".\"{tabela.Replace("\"", "")}\"";
    return await cn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {nome} WHERE ROWNUM = 1") > 0;
}

static async Task<string?> ErrosCompilacao(OracleConnection cn, string sql)
{
    var (tipo, objeto, _, _) = Classificar(sql);
    if (tipo is not ("CREATE OR REPLACE TRIGGER" or "CREATE OR REPLACE VIEW") || objeto == null) return null;
    var nome = objeto.Contains('.') ? objeto.Split('.')[1] : objeto;
    var erros = (await cn.QueryAsync<string>(
        "SELECT line || ':' || position || ' ' || text FROM all_errors WHERE name = :nome AND owner = NVL(:owner, USER) ORDER BY sequence",
        new { nome, owner = objeto.Contains('.') ? objeto.Split('.')[0] : null })).ToList();
    return erros.Count == 0 ? null : string.Join(" | ", erros.Take(3));
}

static void Registrar(string path, string? usuario, string sql, string resultado)
{
    var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{usuario}\t{resultado}\t{Regex.Replace(sql, @"\s+", " ").Trim()}{Environment.NewLine}";
    try { File.AppendAllText(path, linha); } catch { /* log é auxiliar; não interrompe a execução */ }
}

sealed class PranchetaConfig(string connectionString, string ambiente, int porta)
{
    private volatile string connection = connectionString;
    /// <summary>Connection string atual; vazia enquanto ninguém conectou pela tela.</summary>
    public string ConnectionString { get => connection; set => connection = value ?? ""; }
    public string Ambiente { get; } = ambiente;
    public int Porta { get; } = porta;
    public bool PodeExecutar => Ambiente == "desenv";
}

record PedidoConexao(string? Usuario, string? Senha, string? Servidor);

record ComandoSql(string Sql, bool Plsql);

record ResultadoComando(int Ordem, bool Ok, long Ms, string? Erro);

record PedidoScript(string? Owner, List<ComandoSql>? Comandos, string? Confirmacao, bool PermitirDrop);

sealed class NaoConectadoException() : Exception("Não conectado ao Oracle.");

class TabelaEncontrada
{
    public string Owner { get; set; } = "";
    public string Tabela { get; set; } = "";
    public long? Linhas { get; set; }
    public string? Comentario { get; set; }
}

class ColunaCatalogo
{
    public string Tabela { get; set; } = "";
    public string Coluna { get; set; } = "";
    public string Tipo { get; set; } = "";
    public int? Tam { get; set; }
    public int? Prec { get; set; }
    public int? Escala { get; set; }
    public string? Nulo { get; set; }
    public string? Pk { get; set; }
    public string? Fk { get; set; }
    public string? Comentario { get; set; }
}
