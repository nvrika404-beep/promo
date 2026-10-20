(() => {
  'use strict';
  const config = window.RIKA_CONFIG || {};
  const form = document.querySelector('#lead-form');
  const status = document.querySelector('#form-status');
  const button = form.querySelector('button[type="submit"]');
  const fields = ['name', 'phone', 'email', 'consent'];
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  let busy = false, requestId = null, submittedData = null;
  let couponExpiresAt = null;
  const receiptKey = `rika-coupon:${config.campaign || 'rika-exhibition-10'}`;
  const success = document.querySelector('#success');
  const deadline = document.querySelector('#coupon-deadline');
  const copyButton = document.querySelector('#copy-code');
  let deadlineText = '';
  const validReceipt = value => value && typeof value.promo_code === 'string' && value.promo_code.trim() && value.promo_code.length <= 120 && typeof value.expires_at === 'string' && Number.isFinite(Date.parse(value.expires_at));
  function updateCouponStatus() {
    if (couponExpiresAt === null) return;
    const expired = Date.now() >= couponExpiresAt;
    deadline.textContent = expired ? 'Срок действия этого промокода истёк.' : deadlineText;
    deadline.classList.toggle('expired', expired);
    copyButton.disabled = expired;
    if (expired) {
      success.querySelector('h2').textContent = 'Срок скидки истёк.';
    }
  }
  function showCoupon(receipt, scroll = false) {
    couponExpiresAt = Date.parse(receipt.expires_at);
    document.querySelector('#promo-code').textContent = receipt.promo_code;
    success.querySelector('h2').innerHTML = 'Ваши −10%<br>уже здесь.';
    const formatted = new Intl.DateTimeFormat('ru-RU', {timeZone:'Europe/Moscow', day:'numeric', month:'long', year:'numeric', hour:'2-digit', minute:'2-digit'}).format(couponExpiresAt);
    deadlineText = `Действует до ${formatted} (МСК). Одно применение.`;
    form.hidden = true; document.querySelector('#form-intro').hidden = true; success.hidden = false;
    updateCouponStatus();
    if (scroll) {
      success.focus({preventScroll: true});
      success.scrollIntoView({behavior: reduced ? 'instant' : 'smooth', block: 'center'});
    }
  }
  document.addEventListener('visibilitychange', () => {if (!document.hidden) updateCouponStatus();});
  window.addEventListener('focus', updateCouponStatus);
  // Store the code, deadline, and format version only; never contact details.
  try {
    const receipt = JSON.parse(localStorage.getItem(receiptKey));
    if (validReceipt(receipt)) {
      if (receipt.version === undefined && receipt.promo_code === 'Shahanov10') {
        // Match the one-time server migration. Never extend from the current time.
        receipt.expires_at = new Date(Date.parse(receipt.expires_at) + 358 * 86400000).toISOString();
        receipt.version = 2;
        try { localStorage.setItem(receiptKey, JSON.stringify(receipt)); } catch { /* Read-only storage is supported. */ }
      }
      showCoupon(receipt);
    }
  } catch { /* Registration still works when browser storage is unavailable. */ }
  document.querySelector('#another-application').addEventListener('click', () => {
    couponExpiresAt = null;
    try { localStorage.removeItem(receiptKey); } catch { /* Storage may be disabled. */ }
    success.hidden = true; form.hidden = false; document.querySelector('#form-intro').hidden = false;
    document.querySelector('#promo-code').textContent = ''; document.querySelector('#copy-status').textContent = '';
    requestId = null; submittedData = null; status.textContent = ''; form.reset(); form.elements.name.focus();
  });
  document.querySelector('#year').textContent = new Date().getFullYear();
  const uuid = () => crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const n = crypto.getRandomValues(new Uint8Array(1))[0] % 16; return (c === 'x' ? n : (n & 3) | 8).toString(16); });
  const setError = (key, message) => {
    const field = form.elements[key];
    field.setAttribute('aria-invalid', String(Boolean(message)));
    field.setAttribute('aria-describedby', `${key}-error`);
    document.getElementById(`${key}-error`).textContent = message;
  };
  fields.forEach(key => form.elements[key].addEventListener('input', () => setError(key, '')));
  function validate() {
    const name = form.elements.name.value.trim().replace(/\s+/g, ' ');
    const phone = form.elements.phone.value.trim();
    const email = form.elements.email.value.trim();
    const errors = {
      name: name.length < 2 || name.length > 80 ? 'Укажите имя, минимум 2 символа.' : '',
      phone: !/^\+?[0-9\s()\-]{10,24}$/.test(phone) || phone.replace(/\D/g, '').length < 10 || phone.replace(/\D/g, '').length > 15 ? 'Проверьте номер телефона: от 10 до 15 цифр.' : '',
      email: !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 160 ? 'Укажите корректный email, например name@mail.ru.' : '',
      consent: !form.elements.consent.checked ? 'Для обработки заявки нужно ваше согласие.' : '',
    };
    fields.forEach(key => setError(key, errors[key]));
    const first = fields.find(key => errors[key]);
    if (first) { form.elements[first].focus(); return null; }
    const params = new URLSearchParams(location.search);
    const utm = Object.fromEntries(['utm_source', 'utm_medium', 'utm_campaign'].map(k => [k, (params.get(k) || '').slice(0, 100)]));
    return {name, phone, email, consent: true, consent_version: 'promo-2026-10-19', campaign: config.campaign || 'rika-exhibition-10', website: form.elements.website.value, utm};
  }
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (busy) return;
    status.textContent = '';
    const data = validate();
    if (!data) return;
    if (!config.endpoint) {
      status.textContent = 'Регистрация пока не открыта. Данные не отправлены. Свяжитесь с нами: +7 495 128-93-85.';
      return;
    }
    const serialized = JSON.stringify(data);
    if (serialized !== submittedData) { requestId = uuid(); submittedData = serialized; }
    busy = true; button.disabled = true; button.firstElementChild.textContent = 'Сохраняем заявку…';
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(config.endpoint, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({...data, request_id: requestId}), signal: controller.signal, credentials: 'omit', referrerPolicy: 'no-referrer'});
      let result;
      try { result = await response.json(); } catch { throw new Error('Не удалось получить подтверждение. Попробуйте ещё раз — повторная заявка не создастся.'); }
      if (response.status === 409 && result.error === 'phone_already_registered') throw new Error('На этот номер уже выдан промокод. Повторная выдача недоступна, в том числе после окончания срока. Если потеряли код, свяжитесь с нами: +7 495 128-93-85.');
      if (!response.ok || result.ok !== true || !validReceipt(result)) {
        throw new Error(response.status === 429 ? 'Слишком много попыток. Подождите немного и повторите отправку.' : 'Не удалось подтвердить сохранение заявки. Попробуйте ещё раз или позвоните +7 495 128-93-85.');
      }
      const receipt = {promo_code: result.promo_code, expires_at: result.expires_at, version: 2};
      try { localStorage.setItem(receiptKey, JSON.stringify(receipt)); } catch { /* Receipt still appears without storage. */ }
      showCoupon(receipt, true);
      form.reset();
      if (couponExpiresAt > Date.now()) window.dispatchEvent(new Event("rika:success"));
    } catch (error) {
      status.textContent = error.name === 'AbortError' ? 'Ответ задерживается. Попробуйте ещё раз: используем тот же номер заявки, без дубликата.' : error instanceof TypeError ? 'Нет соединения с сервером. Проверьте интернет и повторите отправку.' : error.message;
    } finally {
      clearTimeout(timer); busy = false; button.disabled = false; button.firstElementChild.textContent = 'Да, хочу −10%!';
    }
  });
  document.querySelector('#copy-code').addEventListener('click', async () => {
    updateCouponStatus();
    if (copyButton.disabled) return;
    const code = document.querySelector('#promo-code').textContent;
    try {
      if (navigator.clipboard && isSecureContext) await navigator.clipboard.writeText(code);
      else {
        const input = document.createElement('textarea'); input.value = code; input.style.cssText = 'position:fixed;top:-1000px'; document.body.append(input); input.select();
        let ok; try { ok = document.execCommand('copy'); } finally { input.remove(); }
        if (!ok) throw new Error('copy');
      }
      document.querySelector('#copy-status').textContent = 'Промокод скопирован.';
    } catch { document.querySelector('#copy-status').textContent = 'Выделите промокод и скопируйте вручную.'; }
  });
})();
