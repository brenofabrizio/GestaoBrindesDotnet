# Gbrindes .NET — arquitetura, infraestrutura e segurança

## 1. Objetivo

Este documento descreve a implementação atual do Gbrindes .NET, sua stack, o modelo de publicação no Azure e os mecanismos de segurança identificados no código e no ambiente de homologação.

O sistema .NET é a nova implementação do Controle de Brindes. O sistema PHP legado permanece como referência funcional e não é substituído nem alterado por este projeto.

> **Importante:** este documento não contém senhas, tokens, chaves privadas, strings de conexão ou outros segredos. Valores sensíveis devem ser fornecidos somente pelo ambiente seguro de execução.

## 2. Linguagens e stack

### Aplicação

- **C#** como linguagem principal do backend.
- **ASP.NET Core MVC** para páginas web e endpoints HTTP.
- **Razor/CSHTML** para as views server-side.
- **JavaScript** para interações do navegador, Service Worker e fila offline.
- **CSS/HTML** para a interface web.

### Runtime e bibliotecas principais

- **.NET 10**, definido em `src/Gbrindes.Web/Gbrindes.Web.csproj`.
- **ASP.NET Core Identity** para usuários, papéis, senha e lockout.
- **Entity Framework Core** para persistência e migrations.
- **Npgsql** para PostgreSQL.
- **PostgreSQL** como banco de dados relacional.
- **QRCoder** para QR Code.
- **SkiaSharp** para geração/manipulação de imagens e documentos.
- **xUnit**, `WebApplicationFactory` e testes HTTP para validação automatizada.

### Organização do código

- `src/Gbrindes.Web`: aplicação web, API, domínio e infraestrutura.
- `src/Gbrindes.Web/Controllers`: controllers MVC e API.
- `src/Gbrindes.Web/Data`: entidades, `DbContext` e persistência.
- `src/Gbrindes.Web/Inventory`: regras de estoque, solicitações, TRADE e eventos.
- `src/Gbrindes.Web/Storage`: armazenamento privado de fotos, NF, anexos, assinaturas e logos.
- `src/Gbrindes.Web/Protocols`: protocolos, PDF, QR e verificação.
- `src/Gbrindes.Web/Notifications`: notificações, outbox e envio de e-mail.
- `src/Gbrindes.Web/Imports`: importação, preview e reconciliação.
- `tests/Gbrindes.Tests`: testes unitários, de integração e de endpoints.
- `docs`: decisões, migração, permissões e documentação operacional.

## 3. Como está implementado no Azure

### Modelo de hospedagem identificado

A aplicação está hospedada em uma **Máquina Virtual Linux (Azure VM)**, e não em Azure App Service, Azure Functions ou Kubernetes.

Evidências verificadas no host:

- Hostname do sistema: `vm-hermes-instagram`.
- Fabricante/modelo: Microsoft Corporation / Virtual Machine.
- Virtualização: Microsoft Hyper-V.
- Sistema operacional: Ubuntu 24.04.4 LTS.
- Kernel: Linux Azure.
- Arquitetura: x86-64.

Portanto, a descrição correta é:

> **Aplicação ASP.NET Core executando em uma Azure Virtual Machine Linux, com Nginx como proxy reverso e serviços systemd para manter os processos ativos.**

Não há evidência, nesta documentação, de que o serviço seja uma VPS independente fora do Azure. Uma Azure VM é tecnicamente uma máquina virtual; o termo VPS é genérico e não é o serviço Azure identificado aqui.

### Fluxo de publicação

```text
Internet HTTPS
    |
    v
Nginx na Azure VM :80/:443
    |
    +--> /gbrindes/          -> 127.0.0.1:5087 -> aplicação .NET principal
    |
    +--> /gbrindes-homolog/ -> 127.0.0.1:5088 -> aplicação .NET de homologação
```

O Nginx aplica os prefixos públicos, encaminha os cabeçalhos de proxy e mantém os processos .NET presos ao loopback. As portas `5087` e `5088` não ficam publicadas diretamente na interface externa; o acesso público passa pelo Nginx em HTTPS.

### Aplicação principal

- Prefixo público: `/gbrindes/`.
- Processo ASP.NET Core separado.
- Backend local: `127.0.0.1:5087`.
- A raiz `/gbrindes/` redireciona para o login.

### Homologação isolada

- Prefixo público: `/gbrindes-homolog/`.
- Serviço systemd: `gbrindes-homolog.service`.
- Backend local: `127.0.0.1:5088`.
- Release publicada fora do repositório: `/var/lib/gbrindes-homolog/release`.
- Banco separado: `gbrindes_homolog`.
- Storage separado: `/var/lib/gbrindes-homolog/storage`.
- Key ring separado: `/var/lib/gbrindes-homolog/keys`.
- Arquivo de ambiente protegido pelo sistema operacional.
- SMTP desabilitado/stand-by.

A homologação não utiliza o banco, storage ou key ring do ambiente principal. Nenhum dado de produção foi copiado, alterado ou migrado.

### Serviços operacionais

A homologação possui:

- `gbrindes-homolog.service`: inicia e reinicia a aplicação.
- `gbrindes-homolog-backup.timer`: backup diário, com retenção configurada.
- `gbrindes-homolog-monitor.timer`: monitora o serviço e o readiness público.
- Nginx: termina HTTPS e faz proxy reverso.
- PostgreSQL: banco relacional local, com a porta de desenvolvimento/homologação limitada ao loopback conforme configuração do ambiente.

### Limites desta documentação

A auditoria local confirma a VM, o Nginx, os listeners, os serviços e os caminhos acima. Não foram incluídos neste documento IDs de assinatura Azure, grupo de recursos, IP público, credenciais, certificados, strings de conexão ou configurações secretas.

## 4. Mecanismos de segurança

### 4.1 Autenticação

- Identity para usuários, papéis e credenciais.
- Tokens de acesso revogáveis.
- Logout com invalidação da sessão/token.
- Recuperação e redefinição de senha.
- Troca obrigatória de senha no primeiro acesso quando configurada.
- Lockout após tentativas inválidas.
- Política mínima de senha configurada no código.
- Reset administrativo revoga sessões existentes.

### 4.2 Autorização

- Policies explícitas por permissão.
- Papéis e claims de permissão separados.
- Endpoints protegidos no backend, não apenas no menu da interface.
- Permissões específicas para catálogo, estoque, solicitações, aprovações, eventos, auditoria, relatórios, importação, usuários e configurações.
- Matriz PHP/Laravel mantida separada até homologação; grants não são combinados automaticamente.

### 4.3 Isolamento por escopo

O usuário autenticado pode possuir `IndustryId` e/ou `DepartmentId`. Os serviços aplicam esses vínculos persistidos para impedir acesso ou alteração fora do escopo.

As proteções cobrem, entre outros:

- listagens e detalhes de estoque;
- movimentações e transferências;
- anexos;
- ordens de saída;
- recebimento TRADE;
- retirada TRADE;
- eventos e devoluções;
- entregas, assinatura, PDF e QR;
- relatórios agregados.

Operações cross-industry ou cross-department são rejeitadas de acordo com o contrato da rota, normalmente com `403` ou `404`, sem alterar estoque, status, anexos, NF, protocolo ou auditoria.

### 4.4 Proteção HTTP

O pipeline adiciona headers de segurança, incluindo:

- `Strict-Transport-Security` pelo HSTS fora de Development;
- `X-Content-Type-Options: nosniff`;
- `X-Frame-Options: DENY`;
- `Referrer-Policy: same-origin`;
- `Content-Security-Policy` sem scripts inline autorizados por padrão;
- `frame-ancestors 'none'`;
- `object-src 'none'`;
- `form-action 'self'`;
- `base-uri 'self'`.

O Nginx termina o HTTPS público e encaminha o tráfego para o Kestrel somente no loopback.

### 4.5 Rate limiting e proteção de login

O login possui rate limit por endereço IP:

- janela de 15 minutos;
- limite de 30 tentativas fora do ambiente de testes;
- resposta `429 Too Many Requests` quando o limite é excedido;
- lockout adicional do Identity após falhas consecutivas.

O ambiente de testes relaxa deliberadamente algumas proteções para execução isolada da suíte e não deve ser usado como ambiente publicado.

### 4.6 Proteção de arquivos e uploads

Há storages separados para:

- fotos de itens;
- logo/branding;
- assinaturas digitais;
- anexos de estoque;
- notas fiscais TRADE.

Os arquivos operacionais de homologação ficam fora da árvore versionada. As credenciais e configurações sensíveis também ficam fora do Git e são fornecidas por variáveis/arquivos protegidos do ambiente.

### 4.7 Idempotência e auditoria

Operações críticas de estoque, saída, recebimento e retirada usam chaves de idempotência e hash da requisição para evitar duplicação ou replay inconsistente.

O sistema registra auditoria para operações relevantes, incluindo alterações de estoque, status, aprovações, anexos e ações administrativas.

### 4.8 Banco e migrations

- PostgreSQL é acessado pelo EF Core/Npgsql.
- O schema é versionado por migrations.
- A aplicação em ambiente não-Development exige conexão PostgreSQL configurada.
- Readiness verifica a disponibilidade do banco sem retornar detalhes da conexão.
- Banco de homologação é distinto do banco principal.
- Dados de produção não são usados nos testes de homologação.

### 4.9 Data Protection

O ASP.NET Data Protection utiliza key ring persistente configurável. Em produção/homologação, o caminho deve ser absoluto, durável, restrito e separado por ambiente.

A homologação possui key ring próprio em `/var/lib/gbrindes-homolog/keys`. O código falha ao iniciar em ambiente não-Development se um caminho persistente válido não estiver configurado.

### 4.10 Segredos e Git

O repositório ignora:

- `.env` e variantes;
- `secrets.json`;
- `appsettings.Development.json`;
- bancos locais;
- storage e uploads;
- artefatos de build.

Senhas, tokens, chaves, certificados e strings de conexão não devem ser colocados no repositório, na documentação ou no chat.

## 5. Estado de segurança e pendências

### Confirmado

- Autenticação e autorização no backend.
- Lockout e rate limit de login.
- Headers de segurança e CSP.
- HTTPS público via Nginx.
- Processos .NET isolados no loopback.
- Banco, storage e Data Protection separados na homologação.
- Auditoria e idempotência em operações críticas.
- Isolamento por indústria/departamento em diversos fluxos.
- Backup e monitoramento configurados para homologação.
- Testes automatizados e testes PostgreSQL executados anteriormente sem falhas.

### Ainda precisa de validação ou decisão

- Auditoria completa de escopo em todos os endpoints, aliases, exports, PDFs, QR e downloads.
- Validação visual e funcional em navegador real em desktop, tablet e mobile.
- Validação de restauração completa de backup em ambiente descartável.
- Exercício real do SMTP/outbox; SMTP permanece intencionalmente desligado.
- Integração Lecom, existente no PHP, ainda não portada para o .NET.
- Confirmação da configuração de firewall/NSG do Azure fora do que foi observado localmente.
- Confirmação de rotação e armazenamento dos certificados TLS conforme a política operacional.
- Pipeline formal de publicação e verificação de execução remota do CI.

## 6. Referências do projeto

- `README.md`
- `docs/MIGRATION.md`
- `docs/PERMISSION-MATRICES.md`
- `src/Gbrindes.Web/Program.cs`
- `src/Gbrindes.Web/Gbrindes.Web.csproj`
- `src/Gbrindes.Web/Authentication/`
- `src/Gbrindes.Web/Storage/`
- `src/Gbrindes.Web/Notifications/`
- `src/Gbrindes.Web/Inventory/`
- `tests/Gbrindes.Tests/`
