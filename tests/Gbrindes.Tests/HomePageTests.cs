using Microsoft.AspNetCore.Mvc.Testing;

namespace Gbrindes.Tests;

public sealed class HomePageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HomePageTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Home_page_presents_application_instead_of_template_scaffold()
    {
        var response = await _client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Gestão de Brindes", html);
        Assert.Contains("lang=\"pt-BR\"", html);
        Assert.Contains("Verificar disponibilidade", html);
        Assert.DoesNotContain("Welcome", html);
        Assert.DoesNotContain("Learn about", html);
    }

    [Fact]
    public async Task Home_page_exposes_accessible_live_service_status()
    {
        var response = await _client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"service-status\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("/api/v1/readiness", html);
    }

    [Fact]
    public async Task Shared_navigation_collapses_before_operational_links_overflow()
    {
        var response = await _client.GetAsync("/events");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"app-shell\"", html);
        Assert.Contains("class=\"sidebar", html);
        Assert.Contains("class=\"topbar\"", html);
        Assert.Contains("id=\"mobileNav\"", html);
        Assert.Contains("href=\"/notificacoes\"", html);
    }

    [Fact]
    public async Task Shared_layout_matches_the_legacy_authenticated_shell()
    {
        var response = await _client.GetAsync("/events");
        var html = await response.Content.ReadAsStringAsync();
        var shellJs = await _client.GetStringAsync("/js/app-shell.js");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"app-shell\"", html);
        Assert.Contains("class=\"sidebar", html);
        Assert.Contains("class=\"topbar\"", html);
        Assert.Contains("id=\"mobileNav\"", html);
        Assert.Contains("data-permission=\"dashboard.view\"", html);
        Assert.Contains("data-permission=\"items.view\"", html);
        Assert.Contains("data-permission=\"users.view\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("/js/app-shell.js", html);
        Assert.Contains("/api/v1/auth/me", shellJs);
        Assert.Contains("id=\"logoutBtn\"", html);
        Assert.Contains("/api/v1/auth/logout", shellJs);
    }

    [Fact]
    public async Task Login_page_matches_the_legacy_authentication_shell()
    {
        var response = await _client.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync();
        var loginJs = await (await _client.GetAsync("/js/login.js")).Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("auth-login-shell", html);
        Assert.Contains("auth-login-card", html);
        Assert.Contains("Acesse sua conta", html);
        Assert.Contains("Entre com seus dados corporativos para continuar.", html);
        Assert.Contains("Esqueci minha senha", html);
        Assert.Contains("auth-password-toggle", html);
        Assert.Contains("Acesso protegido e exclusivo para usuários autorizados", html);
        Assert.Contains("Emefarma_Símbolo_Azul Claro.png", html);
        Assert.Contains("Emefarma", html);
        Assert.Contains("href=\"/esqueci-senha\"", html);
        Assert.Contains("localStorage.setItem('gbrindes.auth.token', body.accessToken)", loginJs);
        Assert.Contains("/css/auth.css", html);
    }

    [Fact]
    public async Task Pwa_assets_are_exposed_for_the_legacy_offline_shell()
    {
        var manifest = await _client.GetAsync("/manifest.webmanifest");
        var serviceWorker = await _client.GetAsync("/sw.js");
        var offline = await _client.GetAsync("/offline.html");

        Assert.Equal(System.Net.HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, serviceWorker.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, offline.StatusCode);
        Assert.Contains("Controle de Brindes", await manifest.Content.ReadAsStringAsync());
        Assert.Contains("self.addEventListener", await serviceWorker.Content.ReadAsStringAsync());
        Assert.Contains("Sem conexão com a internet", await offline.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Offline_queue_asset_is_exposed_and_shared_layout_registers_it()
    {
        var queue = await _client.GetAsync("/js/offline-queue.js");
        var events = await _client.GetAsync("/events");
        var queueScript = await queue.Content.ReadAsStringAsync();
        var eventsHtml = await events.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, queue.StatusCode);
        Assert.Contains("indexedDB", queueScript);
        Assert.Contains("api\\/v1", queueScript);
        Assert.Contains("Idempotency-Key", queueScript);
        Assert.Contains("/js/offline-queue.js", eventsHtml);
    }

    [Fact]
    public async Task Stock_adjust_page_exposes_offline_replay_for_idempotent_json_submission()
    {
        var response = await _client.GetAsync("/stock/adjust");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("OfflineQueue.enqueue", html + await _client.GetStringAsync("/js/stock-adjust.js"));
        Assert.Contains("/api/v1/stock/adjustments", html + await _client.GetStringAsync("/js/stock-adjust.js"));
    }

    [Fact]
    public async Task Json_transaction_pages_expose_offline_replay_for_supported_operations()
    {
        var transfer = await _client.GetStringAsync("/js/stock-transfer.js");
        var exitQueue = await _client.GetStringAsync("/js/stock-exit-queue.js");
        var eventDetail = await _client.GetStringAsync("/js/event-detail.js");

        Assert.Contains("OfflineQueue.enqueue", transfer);
        Assert.Contains("OfflineQueue.enqueue", exitQueue);
        Assert.Contains("OfflineQueue.enqueue", eventDetail);
        Assert.Contains("/api/v1/stock/transfers", transfer);
        Assert.Contains("/api/v1/stock/exit-orders/", exitQueue);
        Assert.Contains("/api/v1/events/", eventDetail);
    }

    [Fact]
    public async Task Request_delivery_page_exposes_offline_replay_for_json_delivery_payload()
    {
        var script = await _client.GetStringAsync("/js/request-delivery.js");

        Assert.Contains("OfflineQueue.enqueue", script);
        Assert.Contains("/api/v1/requests/", script);
        Assert.Contains("Idempotency-Key", script);
    }

    [Fact]
    public async Task Notifications_page_matches_the_legacy_inbox_surface()
    {
        var response = await _client.GetAsync("/notificacoes");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Notificações", html);
        Assert.Contains("Marcar todas como lidas", html);
        Assert.Contains("id=\"notifications-login-form\"", html);
        Assert.Contains("id=\"notifications-results\"", html);
        Assert.Contains("/js/notifications.js", html);
    }

    [Fact]
    public async Task Item_import_page_requires_login_before_template_preview_and_apply_controls()
    {
        var response = await _client.GetAsync("/import/items");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Importação de brindes", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("data-import-template", html);
        Assert.Contains("data-import-preview", html);
        Assert.Contains("data-import-apply", html);
        Assert.Contains("data-import-apply disabled", html);
        Assert.Contains("id=\"import-workflow\" class=\"card card-body d-none\"", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("/js/item-import.js", html);
    }

    [Fact]
    public async Task Dashboard_page_requires_login_and_exposes_date_filtered_operational_summary()
    {
        var response = await _client.GetAsync("/dashboard");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Painel de controle", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"dashboard-from\"", html);
        Assert.Contains("id=\"dashboard-to\"", html);
        Assert.Contains("id=\"dashboard-summary\" class=\"d-none\"", html);
        Assert.Contains("/js/dashboard.js", html);
    }

    [Fact]
    public async Task Catalog_page_requires_login_and_exposes_search_status_and_pagination()
    {
        var response = await _client.GetAsync("/items");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Catálogo de brindes", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"catalog-search\"", html);
        Assert.Contains("id=\"catalog-status\"", html);
        Assert.Contains("id=\"catalog-results\" class=\"d-none\"", html);
        Assert.Contains("/js/catalog.js", html);
    }

    [Fact]
    public async Task Catalog_item_detail_page_exposes_authenticated_history_and_optional_stock_positions()
    {
        var itemId = Guid.NewGuid();
        var response = await _client.GetAsync($"/items/{itemId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Detalhes do brinde", html);
        Assert.Contains($"data-item-id=\"{itemId}\"", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("item-history", html);
        Assert.Contains("item-stock-positions", html);
        Assert.Contains("id=\"stock-entry-section\" class=\"card card-body mb-4 d-none\"", html);
        Assert.Contains("id=\"stock-entry-form\"", html);
        Assert.Contains("/js/item-details.js", html);
    }

    [Fact]
    public async Task Catalog_and_item_detail_surfaces_render_the_default_industry()
    {
        var detail = await _client.GetAsync($"/items/{Guid.NewGuid()}");
        var detailHtml = await detail.Content.ReadAsStringAsync();
        var catalogScript = await _client.GetStringAsync("/js/catalog.js");
        var detailScript = await _client.GetStringAsync("/js/item-details.js");

        Assert.Equal(System.Net.HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("Indústria padrão", detailHtml);
        Assert.Contains("id=\"item-industry\"", detailHtml);
        Assert.Contains("\"Indústria\"", catalogScript);
        Assert.Contains("item.industry?.name", catalogScript);
        Assert.Contains("item.industry?.name || \"—\"", detailScript);
    }

    [Fact]
    public async Task User_directory_page_requires_login_and_exposes_search_filters_and_pagination()
    {
        var response = await _client.GetAsync("/users");
        var html = await response.Content.ReadAsStringAsync();
        var script = await _client.GetStringAsync("/js/users.js");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Diretório de usuários", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"users-search\"", html);
        Assert.Contains("id=\"users-active\"", html);
        Assert.Contains("id=\"users-results\" class=\"d-none\"", html);
        Assert.Contains("id=\"users-create-form\"", html);
        Assert.Contains("id=\"users-create-role\"", html);
        Assert.Contains("id=\"users-edit-panel\" class=\"card card-body mb-4 d-none\"", html);
        Assert.Contains("id=\"users-edit-form\"", html);
        Assert.Contains("id=\"users-edit-role\"", html);
        Assert.Contains("id=\"users-edit-role-notice\"", html);
        Assert.Contains("id=\"users-edit-department\"", html);
        Assert.Contains("id=\"users-edit-industry\"", html);
        Assert.Contains("department_id: editDepartmentSelect.value || null", script);
        Assert.Contains("industry_id: editIndustrySelect.value || null", script);
        Assert.Contains("data.departments", script);
        Assert.Contains("data.industries", script);
        Assert.Contains("id=\"users-edit-button\"", html);
        Assert.Contains("id=\"users-edit-password-form\"", html);
        Assert.Contains("id=\"users-edit-password\"", html);
        Assert.Contains("id=\"users-edit-password-button\"", html);
        Assert.Contains("/js/users.js", html);
    }

    [Fact]
    public async Task Account_password_page_exposes_login_and_forced_password_change_flow()
    {
        using var response = await _client.GetAsync("/account/password");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Alteração de senha", html);
        Assert.Contains("id=\"account-password-login-form\"", html);
        Assert.Contains("id=\"account-password-change-form\"", html);
        Assert.Contains("id=\"account-password-current-password\"", html);
        Assert.Contains("/js/account-password.js", html);
    }

    [Fact]
    public async Task Roles_page_requires_login_and_exposes_explicit_grant_editor()
    {
        var response = await _client.GetAsync("/roles");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Perfis e permissões", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"role-list\" class=\"d-none\"", html);
        Assert.Contains("Nenhuma permissão será atribuída automaticamente", html);
        Assert.Contains("/js/roles.js", html);
    }

    [Fact]
    public async Task Events_page_exposes_event_management_surface_without_claiming_api_authorization()
    {
        var response = await _client.GetAsync("/events");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Eventos e feirões", html);
        Assert.Contains("id=\"events-login-form\"", html);
        Assert.Contains("id=\"events-results\"", html);
        Assert.Contains("/js/events.js", html);
        Assert.Contains("A API valida as permissões", html);
    }

    [Fact]
    public async Task Event_detail_page_exposes_legacy_allocation_and_return_readout()
    {
        var eventId = Guid.NewGuid();
        var response = await _client.GetAsync($"/events/{eventId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Detalhes do evento", html);
        Assert.Contains($"data-event-id=\"{eventId}\"", html);
        Assert.Contains("id=\"event-detail-allocations\"", html);
        Assert.Contains("id=\"event-detail-returns\"", html);
        Assert.Contains("id=\"event-detail-return-form\"", html);
        Assert.Contains("id=\"event-detail-return-submit\"", html);
        Assert.Contains("Confirmar devolução ao CD", html);
        Assert.Contains("/js/event-detail.js", html);
    }

    [Fact]
    public async Task Event_detail_page_starts_with_withdrawal_controls_closed()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"event-detail-withdrawal-form\" class=\"card card-body d-none\"", html);
        Assert.Contains("name=\"received_by_name\"", html);
        Assert.Contains("id=\"event-detail-withdrawal-industry\"", html);
        Assert.Contains("id=\"event-detail-withdrawal-signature\"", html);
        Assert.Contains("events.withdraw", html);
        Assert.Contains("Confirmar retirada", html);
    }

    [Fact]
    public async Task Event_detail_page_starts_with_open_and_close_controls_closed()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"event-detail-management-actions\" class=\"card card-body d-none\"", html);
        Assert.Contains("id=\"event-detail-open\"", html);
        Assert.Contains("id=\"event-detail-close\"", html);
        Assert.Contains("events.manage", html);
        Assert.Contains("Abrir evento", html);
        Assert.Contains("Encerrar evento", html);
    }

    [Fact]
    public async Task Event_detail_page_starts_with_allocation_editor_closed()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"event-detail-allocation-editor\" class=\"card card-body mb-3 d-none\"", html);
        Assert.Contains("id=\"event-detail-allocation-industry\"", html);
        Assert.Contains("id=\"event-detail-allocation-item-search\"", html);
        Assert.Contains("id=\"event-detail-allocation-item\"", html);
        Assert.Contains("id=\"event-detail-allocation-quantity\"", html);
        Assert.Contains("id=\"event-detail-allocation-add\"", html);
        Assert.Contains("id=\"event-detail-allocation-rows\"", html);
        Assert.Contains("id=\"event-detail-allocation-submit\"", html);
        Assert.Contains("events.manage", html);
    }

    [Fact]
    public async Task Stock_transfer_page_exposes_location_transfer_without_claiming_event_transfer()
    {
        var response = await _client.GetAsync("/stock/transfer");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Transferir estoque entre locais", html);
        Assert.Contains("name=\"quantity\"", html);
        Assert.Contains("name=\"industry_id\"", html);
        Assert.Contains("name=\"item_id\"", html);
        Assert.Contains("name=\"to_location_id\"", html);
        Assert.Contains("Idempotency-Key", html);
        Assert.Contains("saldo total permanece", html);
        Assert.DoesNotContain("Evento / feirão", html);
        Assert.Contains("/js/stock-transfer.js", html);
    }

    [Fact]
    public async Task Stock_exit_queue_page_requires_confirmation_permission_and_starts_closed()
    {
        var response = await _client.GetAsync("/stock/queue");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Confirmar saídas autorizadas", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("name=\"code\"", html);
        Assert.Contains("id=\"exit-queue-list\" class=\"d-none\"", html);
        Assert.Contains("stock.exit_confirm", html);
        Assert.Contains("/js/stock-exit-queue.js", html);
    }

    [Fact]
    public async Task Stock_exit_page_exposes_authorized_reservation_form_and_cd_separation()
    {
        var response = await _client.GetAsync("/stock/exit");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Solicitar saída autorizada", html);
        Assert.Contains("name=\"item_id\"", html);
        Assert.Contains("name=\"quantity\"", html);
        Assert.Contains("name=\"purpose\"", html);
        Assert.Contains("Idempotency-Key", html);
        Assert.Contains("CD/Estoque", html);
        Assert.Contains("reservada", html);
        Assert.Contains("/js/stock-exit.js", html);
    }

    [Fact]
    public async Task Stock_adjustment_page_requires_explicit_permission_and_exposes_set_and_delta_modes()
    {
        var response = await _client.GetAsync("/stock/adjust");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Ajustar estoque", html);
        Assert.Contains("stock.adjust", html);
        Assert.Contains("name=\"item_id\"", html);
        Assert.Contains("name=\"mode\"", html);
        Assert.Contains("value=\"set\"", html);
        Assert.Contains("value=\"delta\"", html);
        Assert.Contains("name=\"reason\"", html);
        Assert.Contains("Idempotency-Key", html);
        Assert.Contains("/js/stock-adjust.js", html);
    }

    [Fact]
    public async Task Stock_journal_page_requires_login_and_exposes_movement_filters_and_pagination()
    {
        var response = await _client.GetAsync("/stock");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Livro de movimentações", html);
        Assert.Contains("stock.view", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"stock-journal-type\"", html);
        Assert.Contains("id=\"stock-journal-query\"", html);
        Assert.Contains("id=\"stock-journal-from\"", html);
        Assert.Contains("id=\"stock-journal-to\"", html);
        Assert.Contains("id=\"stock-journal-results\" class=\"d-none\"", html);
        Assert.Contains("/js/stock-journal.js", html);
    }

    [Fact]
    public async Task Trade_qr_info_page_keeps_code_and_requires_authenticated_minimal_lookup()
    {
        const string code = "EME-2026-000001";
        var response = await _client.GetAsync($"/consulta-trade/{code}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Consulta TRADE", html);
        Assert.Contains($"id=\"trade-qr-info-page\" data-code=\"{code}\"", html);
        Assert.Contains("id=\"trade-qr-info-login-form\"", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("id=\"trade-qr-info-results\" class=\"card card-body d-none\"", html);
        Assert.Contains("Produtos e saldo disponível", html);
        Assert.Contains("/js/trade-qr-info.js", html);
        Assert.DoesNotContain("Solicitante", html);
        Assert.DoesNotContain("Finalidade", html);
    }
}
