(() => {
  const form = document.getElementById('loginForm');
  const btn = document.getElementById('loginBtn');
  const errEl = document.getElementById('loginGlobalErr');
  document.getElementById('togglePwd')?.addEventListener('click', () => {
    const pwd = document.getElementById('login-password');
    const icon = document.getElementById('eyeIcon');
    const show = pwd.type === 'password';
    pwd.type = show ? 'text' : 'password';
    icon.className = show ? 'bi bi-eye-slash' : 'bi bi-eye';
  });
  function setFieldError(id, msg) {
    const el = document.getElementById('err-' + id);
    const input = form.querySelector('[name="' + id + '"]');
    if (el) el.textContent = msg || '';
    if (input) input.classList.toggle('is-invalid', !!msg);
  }
  function clearErrors() {
    form.querySelectorAll('.is-invalid').forEach(el => el.classList.remove('is-invalid'));
    errEl.classList.add('d-none');
    errEl.textContent = '';
    ['email', 'password'].forEach(id => setFieldError(id, ''));
  }
  form.addEventListener('submit', async ev => {
    ev.preventDefault();
    clearErrors();
    btn.disabled = true;
    btn.innerHTML = '<span class="spin d-inline-block me-2" style="width:14px;height:14px;border:2px solid rgba(255,255,255,.4);border-top-color:#fff;border-radius:50%"></span><span>Entrando…</span>';
    try {
      const base = location.pathname.startsWith('/gbrindes') ? '/gbrindes' : '';
      const response = await fetch(`${base}/api/v1/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({ email: form.email.value, password: form.password.value })
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw body;
      localStorage.setItem('gbrindes.auth.token', body.accessToken);
      const next = new URLSearchParams(location.search).get('next');
      location.href = next && next.startsWith('/') && !next.startsWith('//') ? next : `${base}/dashboard`;
    } catch (e) {
      if (e?.errors) Object.entries(e.errors).forEach(([key, values]) => setFieldError(key, Array.isArray(values) ? values[0] : values));
      else {
        errEl.textContent = e?.message || 'Erro ao fazer login. Tente novamente.';
        errEl.classList.remove('d-none');
      }
    } finally {
      btn.disabled = false;
      btn.innerHTML = '<span>Entrar</span><i class="bi bi-arrow-right" aria-hidden="true"></i>';
    }
  });
})();
