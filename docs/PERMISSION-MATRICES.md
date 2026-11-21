# Matrizes de papéis e permissões — comparação sem homologação

Este documento registra separadamente as matrizes fornecidas pelo legado PHP e pelo serviço Laravel. Nenhuma concessão foi mesclada, semeada ou homologada automaticamente no .NET. Diferenças abaixo exigem decisão explícita antes da importação/mapeamento definitivo.

Fontes consultadas:

- PHP: `D:/Projetos/Gbrindes/database/seed.sql` (catálogo e grants), `app/Controllers/RolesController.php` (listagem/edição) e `app/routes.php` (políticas por rota).
- Laravel: `D:/Projetos/Gbrindes/backend/database/seeders/AccessSeeder.php` (catálogo e grants) e `backend/routes/api.php` (rotas disponíveis; vários endpoints não declaram middleware `permission`).

## Diferenças no catálogo

| Código | PHP | Laravel |
|---|---|---|
| `lookups.purge` | Existe no catálogo e controla purge de cadastros | Ausente do `AccessSeeder`; a rota de purge declara middleware para essa permissão, portanto o grant não é criado pelo seeder |
| Demais códigos | Presentes no seed PHP | Presentes no catálogo de `AccessSeeder` |

## Diferenças de grants por papel

### `admin`

Ambas as fontes concedem todos os códigos do respectivo catálogo ao administrador. Como os catálogos divergem, os conjuntos efetivos também divergem em `lookups.purge`. O bypass runtime do .NET continua limitado às políticas verificadas individualmente (inclui `events.view` e `events.manage`) e não equivale a esse grant total.

### `approver` — TRADE / Gestor

- PHP-only: `requests.view_own`
- Laravel-only: `requests.process`
- Grants iguais: `dashboard.view`, `items.view`, `items.manage`, `stock.view`, `stock.entry`, `stock.exit`, `stock.transfer`, `lookups.view`, `lookups.manage`, `requests.create`, `requests.view_department`, `requests.view_all`, `requests.approve`, `events.view`, `events.manage`, `events.withdraw`, `deliveries.view`, `reports.view`, `reports.export`.

### `operations` — CD / Estoque

- PHP-only: `events.view`, `events.withdraw`, `reports.export`, `alerts.stock`
- Laravel-only: nenhum
- Grants iguais: `dashboard.view`, `items.view`, `stock.view`, `stock.entry`, `stock.exit_confirm`, `stock.transfer`, `stock.receive`, `deliveries.view`, `reports.view`.

### `requester` — TRADE

Sem divergência nos grants: `dashboard.view`, `items.view`, `lookups.view`, `requests.create`, `requests.view_own`.

### `industry` — Indústria

- PHP-only: `stock.view`, `events.view`, `deliveries.view`, `reports.export`
- Laravel-only: nenhum
- Grants iguais: `dashboard.view`, `items.view`, `requests.view_own`, `reports.view`.

## Estado da autorização .NET

O .NET usa claims explícitas em `AspNetRoleClaims` e publica um catálogo limitado às permissões já reconhecidas pela API, incluindo `items.purge`, `events.view`, `events.manage`, `events.withdraw`, `deliveries.view`, `audit.view` e `import.run`. O endpoint de gestão não cria grants para papéis existentes, não sincroniza matriz de origem e rejeita permissões fora do catálogo .NET. `admin` não pode ser editado; a resposta mostra claims explícitas e não inventa uma lista de acesso total. `roles.manage` exige claim explícita e não tem bypass automático de `admin`. O bypass runtime de `admin` cobre somente policies explicitamente mapeadas ao legado: `events.view`, `events.manage`, `deliveries.view` e `stock.transfer_or_exit` (apenas no alias PHP de transferência); não cria claims nem implementa outros `events.*`/`deliveries.*`.

Há divergência nominal/funcional que ainda precisa de homologação: o .NET usa `approval_rules.manage`, enquanto o seed PHP usa `rules.manage`; `deliveries.view` protege a listagem `GET /api/v1/deliveries`, o detalhe `GET /api/v1/deliveries/{id}`, a assinatura privada `GET /api/deliveries/{id}/signature`, o PDF `GET /api/v1/deliveries/{id}/pdf` e o QR `GET /api/v1/deliveries/{id}/qr`, com escopo por indústria e sem exposição de caminhos/hash. A URL assinada do QR expira em 7 dias; `GET /api/v1/verify/{code}` é anônimo apenas com assinatura válida, valida protocolo e retorna dados mínimos. O PDF incorpora o mesmo QR. Produção depende de `DeliveryVerification:PublicBaseUrl` HTTPS incluído em `AllowedHosts` e de `DataProtection:KeysPath` persistente, ainda sem verificação de deployment. `audit.view` protege a consulta da trilha, enquanto `reports.view` e `reports.export` protegem os relatórios implementados. Nenhuma dessas permissões representa homologação ou importação das matrizes de origem. Em usuários, a referência PHP permite `GET /api/users/options` a qualquer sessão autenticada; o alvo preserva essa distinção em `GET /api/v1/users/options` (autenticação básica, sem `users.view`). Já `GET /api/v1/users/create-options`, `POST /api/v1/users`, `PUT /api/v1/users/{id}` (inclusive reset administrativo de senha), ativar/desativar, lixeira, restauração e purge exigem `users.manage`; listagem e detalhe exigem `users.view`. O reset de senha força troca no próximo acesso e revoga sessões existentes. Essas são políticas runtime do alvo, sem concessão ou combinação automática de grants. `events.view` protege listagem/detalhe; `events.manage` protege manutenção de eventos/cotas; `events.withdraw` protege `POST /api/v1/events/{id}/withdrawals` por claim explícita, sem bypass de `admin`. A rota de devolução `POST /api/v1/events/{id}/returns` aceita `events.manage` (inclui o bypass PHP restrito de `admin`) ou claim explícita `stock.transfer`, conforme as alternativas da rota PHP. Transferência mantém autorização separada por contrato: `POST /api/v1/stock/transfers` exige `stock.transfer`, enquanto o alias PHP `POST /api/stock/transfers` aceita `stock.transfer` OU `stock.exit` e bypass restrito de `admin`; as policies não alteram nem combinam grants das matrizes. Nenhuma permissão foi importada ou combinada. A UI de administração de papéis deve indicar claramente quais permissões são operacionais até que o mapa final seja aprovado.

## Próxima decisão de homologação

Aprovar, por fonte e por papel, o conjunto canônico e o mapeamento das permissões antes de importar usuários/papéis ou conceder grants em produção. Até essa decisão, os seeds legados permanecem somente como referência; nenhuma matriz combinada deve ser tratada como autorizada.
