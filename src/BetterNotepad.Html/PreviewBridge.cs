namespace BetterNotepad.Html;

/// <summary>
/// The script every preview document gets (added with AddScriptToExecuteOnDocumentCreated, so a page's own CSP cannot block it).
/// Source line ↔ scroll position from the data-bn-line marks (monotonic piecewise-linear map, proportional fallback),
/// double-click → source line, scroll restore after a live refresh, SVG fit / zoom. No timers: layout changes mark the map stale
/// through a ResizeObserver and it is rebuilt on the next use. Per-render settings come from &lt;meta name="bn-preview"&gt;.
/// </summary>
internal static class PreviewBridge
{
    public const string MetaName = "bn-preview";

    public const string Script = """
(function () {
  'use strict';
  if (window.top !== window || window.__bnBridge) return;
  window.__bnBridge = true;
  var host = window.chrome && window.chrome.webview;
  var cfg = { lines: 1, svg: false, restore: null };
  var marks = null, stale = true, expectY = null, pending = false, userScrolled = false, zoom = 1;
  try { history.scrollRestoration = 'manual'; } catch (e) { }

  function post(o) { if (host) host.postMessage(o); }
  function root() { return document.scrollingElement || document.documentElement; }
  function maxY() { var r = root(); return Math.max(0, r.scrollHeight - window.innerHeight); }
  function docEnd() { return Math.max(root().scrollHeight, 1); }

  function readCfg() {
    var m = document.querySelector('meta[name="bn-preview"]');
    if (!m) return;
    try { var c = JSON.parse(m.getAttribute('content')); for (var k in c) cfg[k] = c[k]; } catch (e) { }
  }

  function collect() {
    var list = document.querySelectorAll('[data-bn-line]');
    var sy = window.scrollY, out = [], lastL = -1, lastT = -1e9;
    for (var i = 0; i < list.length; i++) {
      var el = list[i];
      if (!el.getClientRects().length) continue;
      var l = +el.getAttribute('data-bn-line');
      var t = el.getBoundingClientRect().top + sy;
      if (l <= lastL || t < lastT) continue;
      out.push({ l: l, t: t }); lastL = l; lastT = t;
    }
    marks = out; stale = false;
  }
  function ensure() { if (stale || !marks) collect(); return marks; }

  function yForLine(x) {
    var m = ensure(), n = m.length, lines = Math.max(cfg.lines, 1);
    if (n < 2) return null;
    if (x <= m[0].l) return m[0].t * Math.max(0, (x - 1) / Math.max(1, m[0].l - 1));
    var lo = 0, hi = n - 1;
    while (lo < hi) { var mid = (lo + hi + 1) >> 1; if (m[mid].l <= x) lo = mid; else hi = mid - 1; }
    var a = m[lo], b = m[lo + 1];
    if (!b) return a.t + (docEnd() - a.t) * Math.min(1, (x - a.l) / Math.max(1, lines + 1 - a.l));
    return a.t + (b.t - a.t) * ((x - a.l) / (b.l - a.l));
  }

  function lineForY(y) {
    var m = ensure(), n = m.length, lines = Math.max(cfg.lines, 1), x;
    if (n < 2) {
      var my = maxY();
      x = 1 + (lines - 1) * (my > 0 ? y / my : 0);
    } else if (y + 1 < m[0].t) {
      x = 1 + (m[0].l - 1) * (m[0].t > 0 ? y / m[0].t : 0);
    } else {
      var lo = 0, hi = n - 1;
      while (lo < hi) { var mid = (lo + hi + 1) >> 1; if (m[mid].t <= y + 1) lo = mid; else hi = mid - 1; }
      var a = m[lo], b = m[lo + 1];
      if (!b) x = a.l + (lines + 1 - a.l) * Math.min(1, (y - a.t) / Math.max(1, docEnd() - a.t));
      else x = a.l + (b.l - a.l) * (b.t > a.t ? Math.max(0, y - a.t) / (b.t - a.t) : 0);
    }
    x = Math.max(1, Math.min(lines + 0.999, x));
    return { line: Math.floor(x), frac: x - Math.floor(x) };
  }

  function scrollToLine(line, frac, sf) {
    var my = maxY(), y = yForLine(line + (frac || 0));
    if (y === null) y = (sf || 0) * my;
    if (sf >= 0.999) y = my;
    y = Math.max(0, Math.min(my, y));
    if (Math.abs(y - window.scrollY) < 1) return;
    expectY = y;
    window.scrollTo(0, y);
  }

  window.addEventListener('scroll', function () {
    if (pending || cfg.svg) return;
    pending = true;
    requestAnimationFrame(function () {
      pending = false;
      var y = window.scrollY;
      if (expectY !== null && Math.abs(y - expectY) < 2) { expectY = null; return; }
      expectY = null;
      userScrolled = true;
      var r = lineForY(y);
      post({ t: 's', line: r.line, frac: r.frac, y: y });
    });
  }, { passive: true });

  document.addEventListener('dblclick', function (e) {
    var el = e.target;
    while (el && el.nodeType === 1 && !el.hasAttribute('data-bn-line')) el = el.parentElement || el.parentNode;
    if (el && el.nodeType === 1) post({ t: 'line', line: +el.getAttribute('data-bn-line') });
    else if (cfg.svg) { zoom = 1; fitSvg(); }
  });

  if (host) host.addEventListener('message', function (e) {
    var d = e.data || {};
    if (d.t === 'to') scrollToLine(d.line, d.frac, d.sf);
  });

  // ── SVG: fit into the window (vector, so small icons are scaled up too), Ctrl+wheel zooms ──
  function fitSvg() {
    var s = document.querySelector('.bn-stage > svg'), stage = document.querySelector('.bn-stage');
    if (!s || !stage) return;
    var vb = s.viewBox && s.viewBox.baseVal;
    var w = parseFloat(s.getAttribute('width')), h = parseFloat(s.getAttribute('height'));
    if (/%/.test(s.getAttribute('width') || '')) w = NaN;
    if (/%/.test(s.getAttribute('height') || '')) h = NaN;
    var hasVb = vb && vb.width > 0 && vb.height > 0;
    if (!(w > 0 && h > 0)) {
      if (hasVb) {
        if (w > 0) h = w * vb.height / vb.width;
        else if (h > 0) w = h * vb.width / vb.height;
        else { w = vb.width; h = vb.height; }
      } else { w = w > 0 ? w : 300; h = h > 0 ? h : 150; }
    }
    if (!hasVb) s.setAttribute('viewBox', '0 0 ' + w + ' ' + h);
    var pad = 28, k = Math.min((window.innerWidth - 2 * pad) / w, (window.innerHeight - 2 * pad) / h) * zoom;
    k = Math.max(k, 0.01);
    s.style.width = (w * k) + 'px'; s.style.height = (h * k) + 'px';
    document.body.classList.toggle('bn-big', zoom > 1.001);
  }
  window.addEventListener('wheel', function (e) {
    if (!cfg.svg || !e.ctrlKey) return;
    e.preventDefault();
    zoom = Math.max(0.1, Math.min(40, zoom * Math.pow(1.0015, -e.deltaY)));
    fitSvg();
  }, { passive: false });

  window.addEventListener('resize', function () { stale = true; if (cfg.svg) fitSvg(); });

  function restore() {
    var r = cfg.restore;
    if (!r) return;
    if (r.line > 0) scrollToLine(r.line, r.frac || 0, r.sf || 0);
    else if (r.y > 0) { expectY = r.y; window.scrollTo(0, r.y); }
  }

  document.addEventListener('DOMContentLoaded', function () {
    readCfg();
    if (cfg.svg) fitSvg();
    try { new ResizeObserver(function () { stale = true; }).observe(document.documentElement); } catch (e) { }
    restore();
    post({ t: 'ready' });
  });
  window.addEventListener('load', function () {
    stale = true;
    if (cfg.svg) fitSvg();
    // late images / fonts moved things: restore again unless the user scrolled meanwhile
    if (!userScrolled) restore();
    post({ t: 'loaded' });
  });
})();
""";
}
