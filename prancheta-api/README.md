# prancheta-api

API local da **Prancheta Database** (.NET 10, Minimal API + Dapper + Oracle.ManagedDataAccess.Core)
que liga a página `prancheta.html` ao Oracle. Ela só escuta em `127.0.0.1` e só aceita chamadas da
própria página (aberta como arquivo local ou servida em `localhost`).

A página abre numa tela de conexão (servidor, usuário, senha) e só libera a prancheta depois de
conectar. A senha vai direto para esta API e fica só na memória dela; o navegador guarda apenas
servidor e usuário. Fechar a API ou clicar em **Desconectar** descarta a conexão.

## Como rodar

No PowerShell, dentro desta pasta:

```powershell
dotnet run -- --desenv   # sem --desenv a API só lê (catálogo e conferência)
```

A API serve a própria tela em `http://127.0.0.1:5080/` (o `prancheta.html` vai embutido no
assembly) e abre o navegador nesse endereço. A página também funciona aberta direto do disco.

Opções de linha de comando (valem igual para o `Abrir Prancheta.cmd` do pacote gerado por `publicar.ps1`):

| Opção | Variável de ambiente | Efeito |
|---|---|---|
| `--desenv` | `PRANCHETA_AMBIENTE=desenv` | Libera `/api/executar`. |
| `--porta=5090` | `PRANCHETA_PORTA=5090` | Troca a porta (padrão `5080`). |
| `--sem-navegador` | — | Não abre o navegador ao subir. |
| — | `PRANCHETA_ORACLE` | Connection string; a API já sobe conectada. Vale só para aquela janela. |

- Se a porta já estiver em uso (a prancheta já aberta), a segunda instância só abre o navegador
  no endereço existente e encerra.
- Não coloque a string de conexão em `appsettings.json` nem em arquivo versionado.
- Com a página aberta do disco e porta diferente de `5080`, ajuste o endereço em **Avançado** na
  tela de conexão (servida pela API, a página usa a própria origem e esconde essa opção).

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| POST | `/api/conectar` | Recebe `{servidor, usuario, senha}`, abre a conexão para testar e guarda em memória. 401 com a mensagem `ORA-` se falhar. |
| POST | `/api/desconectar` | Descarta a conexão em memória e limpa o pool. |
| GET | `/api/saude` | Testa a conexão; devolve usuário, banco, versão e se a execução está liberada. |
| GET | `/api/tabelas?nome=&owner=` | Pesquisa tabelas pela parte do nome (`%`/`*` como coringa) em todos os owners visíveis, até 200. |
| GET | `/api/tabela?owner=&nome=` | Estrutura de uma tabela: colunas, tipos, PK, FK e comentários. |
| GET | `/api/catalogo?owner=&filtro=` | Lê `ALL_TAB_COLUMNS` + PK/FK do owner (padrão: usuário da conexão). `filtro` aceita `*` ou `%`. |
| POST | `/api/analisar` | Dry-run: compara cada comando do script com o dicionário (objeto já existe, tabela inexistente, coluna NOT NULL sem DEFAULT em tabela com linhas). Não executa nada. |
| POST | `/api/executar` | Executa o script em ordem e para no primeiro erro. Só com `PRANCHETA_AMBIENTE=desenv` e com a confirmação `EXECUTAR` digitada na tela. |

## Travas de segurança

- Escuta apenas em `127.0.0.1`; CORS só para origem `null` (arquivo local) e `localhost`/`127.0.0.1`.
- Execução bloqueada (HTTP 403) fora do ambiente `desenv`.
- Lista de comandos permitidos: `CREATE [OR REPLACE] TABLE | SEQUENCE | [UNIQUE] INDEX | VIEW | TRIGGER`,
  `ALTER TABLE` e `COMMENT ON`. `DROP` e `ALTER ... DROP/RENAME/MOVE` ficam bloqueados.
- Cada execução é registrada em `execucoes.log` (data/hora, usuário, resultado, SQL), sem credenciais.
- As mensagens `ORA-` são devolvidas à tela de propósito, para depurar o DDL. É uma ferramenta local
  de desenvolvimento; não publique esta API em servidor.

## Teste rápido

```powershell
curl.exe -s http://127.0.0.1:5080/api/saude
```

Antes de conectar pela tela, a resposta é `{"conectado":false,...,"erro":"Não conectado ao Oracle."}`.
