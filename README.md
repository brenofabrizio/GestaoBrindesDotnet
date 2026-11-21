# Gbrindes .NET

Nova implementação do Controle de Brindes em C# com ASP.NET Core MVC e API versionada. O destino consolida os fluxos do sistema web PHP legado e os contratos/recursos da API Laravel em uma aplicação, sem modificar os projetos de origem.

## Estado

Esta é uma base em desenvolvimento, não uma versão substituta do sistema de produção. Já existem API de saúde/readiness, autenticação parcial com tokens revogáveis, endpoints protegidos de categorias, departamentos, locais, indústrias, fornecedores, brindes e estoque, e migrations para identidade, catálogo, estoque e rascunhos de solicitações. Migrations atuais de identidade, catálogo, estoque e solicitações foram aplicadas e verificadas no PostgreSQL 17 local; testes de integração dedicados validam persistência, fluxo de solicitações e aprovação com Npgsql. A API de estoque persiste entradas/saídas/ajustes/estornos e movimentos append-only, com transação, idempotência e bloqueio de saldo disponível; ajustes set/delta respeitam reservas, estornos são compensatórios e ambas as operações exigem chave de idempotência. A maioria dos testes usa EF InMemory e não prova garantias específicas de concorrência do PostgreSQL; há testes de integração separados que validam criação, consulta/listagem, edição, envio, reserva, persistência e o fluxo de aprovação parcial com Npgsql. Estoque continua parcial: faltam transferências, fluxo completo de reservas, posições por local e integração completa com o catálogo. Solicitações internas têm criação/detalhe/edição de rascunho, listagem com filtros/paginação, submissão com autoaprovação ou roteamento pela primeira regra correspondente (quantidade, valor, categoria, departamento ou perfil), decisão de aprovação/reprovação justificada para o aprovador atribuído, lista/detalhe limitados por solicitante, escopo departamental, visão global ou atribuição individual/perfil do aprovador, aprovação parcial de quantidades e reserva após aprovação, além de transições autorizadas de separação (`aprovada → em_separacao → pronta`). Cancelamento grava motivo/data/histórico e libera reservas transacionalmente; o proprietário pode cancelar exceto em separação/aprovada, enquanto `requests.cancel_any` autoriza qualquer estado aberto. O papel reservado `admin` do fluxo PHP tem exceção runtime limitada a visualizar, decidir e processar solicitações, cancelar estados abertos e gerir regras de aprovação; a autoaprovação permanece bloqueada e nenhuma permissão é semeada/mesclada automaticamente. Regras podem ser listadas, criadas, editadas parcialmente, desativadas e excluídas logicamente. Regras multi-etapa, homologação completa do `ApprovalEngine` legado, demais transições de entrega/cancelamento, interface e escopo por departamento/indústria/atribuição continuam pendentes. Categorias e brindes têm filtros e lixeira/restauração; brindes podem ser vinculados a local/fornecedor e sua exclusão é bloqueada enquanto houver saldo/reservas. Faltam purge seguro com validação de referências, fotos e auditoria. Ainda faltam administração de contas, recuperação de senha, demais fluxos operacionais, TRADE, entregas, relatórios e integrações.

As matrizes de permissões do PHP legado e do Laravel permanecem separadas até homologação; não conceder nem combinar os perfis automaticamente durante a migração.

Não foram copiados bancos de dados, usuários, arquivos operacionais, backups, `.env` nem credenciais dos projetos PHP ou Django.

## Requisitos

- Windows 10/11 x64.
- .NET SDK 10.0.403 (fixado em `global.json`; patch posterior da linha 10.0 permitido).
- PostgreSQL 17 com ferramentas de linha de comando instaladas em `C:\Program Files\PostgreSQL\17\bin`.
- PowerShell 5.1 ou posterior.
- A ferramenta local `dotnet-ef` 10.0.14 é restaurada pelo manifesto versionado `dotnet-tools.json`.

## Executar

Na pasta `D:\Projetos\Gbrindes_dotnet`, restaure dependências e rode os testes rápidos:

```powershell
dotnet restore
dotnet test
dotnet run --project src/Gbrindes.Web
```

Para preparar um PostgreSQL local descartável, aplicar migrations e executar também o teste de integração contra PostgreSQL:

```powershell
.\scripts\dev.ps1 start
.\scripts\dev.ps1 test
```

O script mantém o cluster de teste fora do repositório em `%LOCALAPPDATA%\Gbrindes\postgres-data`, usa a porta `55432` e escuta somente em `127.0.0.1`. A autenticação `trust` é restrita ao loopback e serve apenas para este banco de desenvolvimento descartável; nunca reutilize essa configuração em produção ou exponha a porta à rede. Os logs ficam em `%LOCALAPPDATA%\Gbrindes\logs`. Para desligar o servidor, use `.\scripts\dev.ps1 stop`; para conferir, use `.\scripts\dev.ps1 status`.

O banco `gbrindes_dev` é usado apenas pelas ações `start` e `migrate`; `test` não o altera. Cada execução de `test` cria um banco PostgreSQL temporário com nome aleatório, aplica as migrations, executa as suítes padrão e PostgreSQL, e remove somente esse banco no `finally`. O cluster existente não é apagado nem sobrescrito. Não armazene dados reais no cluster local de desenvolvimento.

O `WebApplicationFactory` usa o ambiente ASP.NET Core `Testing`, que relaxa os limites de login por IP e as verificações de configuração próprias de produção para permitir testes isolados; esse ambiente é exclusivo do host de teste e não deve ser usado em implantação.

O endpoint `GET /api/v1/health` verifica somente a vida do processo. `GET /api/v1/readiness` verifica a conexão PostgreSQL e retorna `503` quando o banco não estiver configurado ou disponível; nenhum detalhe de conexão é devolvido.

Configure a conexão localmente por variável de ambiente, sem gravar segredos no repositório:

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Database=gbrindes;Username=gbrindes_app;Password=<segredo-local>"
```

As migrations atuais criam tabelas de autenticação/sessões, fatias do catálogo (categorias, departamentos, locais, indústrias, fornecedores e brindes), estoque (saldo e movimentos) e solicitações internas. O esquema de negócio ainda está incompleto. Para gerar/aplicar migrations, use `dotnet ef migrations add <Nome>` e `dotnet ef database update` somente em um PostgreSQL local de desenvolvimento, nunca apontando para o banco de produção. Não conecte esta base a dados reais nem a publique como substituta do sistema atual.

## Configuração operacional pendente para produção

A aplicação falha ao iniciar em `Production` se PostgreSQL, `AllowedHosts`, o domínio de verificação HTTPS ou um caminho absoluto para o key ring persistente de ASP.NET Data Protection não estiverem configurados. Use `DeliveryVerification__PublicBaseUrl` com o domínio HTTPS público incluído em `AllowedHosts` e `DataProtection__KeysPath` apontando para armazenamento durável e compartilhado entre réplicas. O key ring deve sobreviver a reinícios, ser compartilhado entre réplicas e ter permissões restritas e proteção em repouso apropriada; a configuração e a validação em deployment ainda não foram feitas. Forneça credenciais do banco e demais segredos somente pelo mecanismo seguro do ambiente, nunca por arquivos versionados.

## Estrutura

- `src/Gbrindes.Web`: aplicação ASP.NET Core MVC, endpoints e domínio migrado.
- `tests/Gbrindes.Tests`: testes unitários e HTTP.
- `docs/MIGRATION.md`: escopo, situação por domínio, pendências consolidadas, gates/decisões bloqueadas, evidências de teste e critérios de aceite.
