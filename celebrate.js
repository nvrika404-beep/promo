(() => {
  'use strict';
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const toggle = document.querySelector('#motion-toggle');
  const canvas = document.querySelector('#confetti');
  const context = canvas.getContext('2d');
  let paused = reduced.matches, frame = 0;
  const stopConfetti = () => { cancelAnimationFrame(frame); context?.clearRect(0, 0, canvas.width, canvas.height); };
  function applyMotion() {
    document.body.classList.add('motion-ready');
    document.body.classList.toggle('motion-paused', paused);
    toggle.setAttribute('aria-pressed', String(paused));
    toggle.setAttribute('aria-label', paused ? 'Включить анимацию' : 'Остановить анимацию');
    toggle.title = paused ? 'Включить анимацию' : 'Остановить анимацию';
    if (paused) stopConfetti();
  }
  function celebrate() {
    if (paused || reduced.matches || !context || document.hidden) return;
    stopConfetti();
    const width = document.documentElement.clientWidth;
    const height = Math.min(1000, Math.max(innerHeight, 650));
    const ratio = Math.min(devicePixelRatio || 1, 2);
    canvas.width = width * ratio; canvas.height = height * ratio;
    canvas.style.height = height + 'px';
    canvas.style.top = Math.max(0, scrollY - document.querySelector('.masthead').offsetHeight) + 'px';
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    const colors = ['#fff7e8', '#14291e', '#f5cd59', '#b9d0a1'];
    const pieces = Array.from({length: width < 600 ? 42 : 74}, (_, i) => ({
      x: Math.random() * width, y: -40 - Math.random() * height * .65,
      speed: 150 + Math.random() * 150, drift: (Math.random() - .5) * 70,
      angle: Math.random() * Math.PI, spin: (Math.random() - .5) * 5,
      size: 4 + Math.random() * 5, color: colors[i % colors.length], round: i % 4 === 0,
    }));
    const start = performance.now();
    function draw(now) {
      const elapsed = (now - start) / 1000;
      context.clearRect(0, 0, width, height);
      if (elapsed > 4.4 || paused || document.hidden) return;
      context.globalAlpha = Math.min(.9, Math.max(0, (4.4 - elapsed) / 1.1));
      for (const p of pieces) {
        const y = p.y + p.speed * elapsed;
        context.save();
        context.translate(p.x + Math.sin(elapsed * 2 + p.angle) * 24 + p.drift * elapsed, y);
        context.rotate(p.angle + p.spin * elapsed);
        context.fillStyle = p.color;
        if (p.round) {context.beginPath();context.arc(0, 0, p.size / 2, 0, Math.PI * 2);context.fill();}
        else context.fillRect(-p.size / 2, -p.size, p.size, p.size * 1.7);
        context.restore();
      }
      frame = requestAnimationFrame(draw);
    }
    frame = requestAnimationFrame(draw);
  }
  toggle.addEventListener('click', () => {paused = !paused; applyMotion(); if (!paused) celebrate();});
  reduced.addEventListener('change', () => {paused = reduced.matches; applyMotion();});
  document.addEventListener('visibilitychange', () => {if (document.hidden) stopConfetti();});
  window.addEventListener('rika:success', celebrate);
  applyMotion();
  celebrate();
})();
