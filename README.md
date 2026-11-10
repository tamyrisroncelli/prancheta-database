# Prancheta Database

Ferramenta local para modelar tabelas Oracle a partir dos nomes dos atributos. Você descreve a
estrutura (transações, níveis, chaves, atributos e regras) e a prancheta sugere tipos, chaves
estrangeiras e validações, gera o script Oracle (com rollback) e, opcionalmente, confere e executa
o script num banco de desenvolvimento.

São duas peças:

| Pasta / arquivo | O que é |
|---|---|
| `prancheta.html` | A tela. Uma página única, sem build e sem dependências externas. |
| `prancheta-api/` | API local em .NET 10 que faz a ponte entre a tela e o Oracle. |

## Recursos

- **Tela de conexão**: a prancheta só abre conectada ao Oracle. A senha vai para a API local e fica
  só na memória dela; o navegador guarda apenas servidor e usuário.
- **Estrutura**: grid de atributos com tipo, descrição, fórmula e obrigatoriedade; subníveis viram
  tabelas filhas com chave composta.
- **Sugestões pelo nome**: o sufixo do atributo sugere tipo e descrição (`...Cod`, `...Nom`,
  `...Dat`, `...Vlr`, `...Cpf`...); chaves estrangeiras são deduzidas quando uma tabela contém a
  chave inteira de outra.
- **Regras**: `error`, `msg`, `default`, `noaccept`, `required`, `unique`, `serial`, `add`,
  `subtract`, com condição e momento. Cada regra vai para o banco quando possível (CHECK, DEFAULT,
  NOT NULL, UNIQUE, TRIGGER) e para o contrato da tela.
- **Pesquisar tabelas**: procura tabelas existentes no Oracle pelo nome e cria uma nova transação
  copiando colunas, tipos, PK, obrigatoriedade e comentários.
- **Script Oracle**: criação e rollback, como script, migration Flyway (V/U) ou Liquibase. Com o
  catálogo do banco carregado, tabela existente vira `ALTER TABLE ADD`.
- **Conferir no banco**: compara cada comando do script com o dicionário de dados sem executar nada.
- **Executar**: só com a API iniciada em modo `desenv` e com a palavra `EXECUTAR` digitada na tela.
- **Saídas extras**: OpenAPI, DTO C# com validação e formulário (JSON Schema) com prévia.
- Tema claro, escuro ou automático.

## Como usar (executável, sem instalar nada)

1. Baixe `PranchetaDatabase.exe` na página **Releases** do repositório (Windows 10/11, 64 bits).
2. Dê duplo clique. Abre uma janela de console (é a API) e o navegador na tela de conexão.
3. Preencha servidor (`host:1521/servico`), usuário e senha.
4. Para encerrar, feche a janela de console.

O `.exe` já traz o runtime do .NET e a tela embutida; não precisa do SDK.

- Para liberar o botão **Executar** (só em banco de desenvolvimento), abra pelo PowerShell:
  `.\PranchetaDatabase.exe --desenv`
- Outras opções: `--porta=5090` (outra porta) e `--sem-navegador` (não abre o navegador).
- Abrir o `.exe` de novo com ele já aberto só reabre o navegador na mesma tela.
- O executável não é assinado: o Windows SmartScreen pode avisar na primeira vez
  ("Mais informações" → "Executar assim mesmo"). Em máquina com política corporativa, o
  antivírus ou a política pode bloquear executáveis fora de pastas permitidas.
- Os projetos ficam no `localStorage` do endereço `http://127.0.0.1:5080`, separado do da página
  aberta como arquivo. Para levar projetos de um modo para o outro, use **Projeto (JSON)**.

## Como rodar pelo código-fonte

Requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download) e acesso a um Oracle (12c ou mais novo).

```powershell
cd prancheta-api
dotnet run -- --desenv   # sem --desenv a API só lê (catálogo e conferência)
```

Depois abra `http://127.0.0.1:5080/` (ou `prancheta.html` direto do disco) e preencha servidor
(`host:1521/servico`), usuário e senha.

## Gerar o executável

```powershell
.\publicar.ps1
```

Gera `dist\PranchetaDatabase.exe` (single-file, self-contained, win-x64, ~50 MB). Anexe esse
arquivo numa Release do GitHub; a pasta `dist/` não é versionada.

Detalhes da API, endpoints e travas de segurança: [prancheta-api/README.md](prancheta-api/README.md).

## Segurança

- A API escuta só em `127.0.0.1` e aceita chamadas só da página local.
- Nenhuma credencial é gravada em arquivo, devolvida pela API ou escrita em log.
- A execução aceita só DDL de estrutura (`CREATE TABLE/SEQUENCE/INDEX/VIEW/TRIGGER`, `ALTER TABLE`,
  `COMMENT ON`); `DROP`, DML e `GRANT` são recusados.
- Use a execução apenas em banco de desenvolvimento. O DDL do Oracle faz commit implícito: o que
  já rodou não volta com rollback de transação (use o script de rollback gerado).

## Limites

- As sugestões são regras de nome, sem modelo de IA.
- O diff com o banco cobre colunas novas e tipo divergente; não remove colunas nem converte dados.
- Os dados ficam no `localStorage` do navegador; use **Projeto (JSON)** para exportar e versionar.
