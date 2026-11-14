// JellyXR cast timeline: a section on movie / episode pages of jellyfin-web, plus the renderer
// the plugin settings page uses (window.JellyXR.render).
(function () {
  'use strict';
  if (window.JellyXR) { return; }

  var STYLE = [
    '.jxr{margin:1.5em 0}',
    '.jxr h2{margin:0 0 .2em}',
    '.jxr-sub{opacity:.65;font-size:.85em;margin-bottom:1em}',
    '.jxr-row{display:flex;align-items:center;gap:.8em;margin:.45em 0}',
    '.jxr-face{width:2.6em;height:2.6em;border-radius:50%;flex:none;background:rgba(128,128,128,.3) center/cover no-repeat}',
    '.jxr-who{flex:0 0 13em;min-width:0;line-height:1.2}',
    '.jxr-who b,.jxr-who span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
    '.jxr-who span{opacity:.65;font-size:.85em}',
    '.jxr-bar{position:relative;flex:1;height:.8em;border-radius:.4em;background:rgba(128,128,128,.22);overflow:hidden}',
    '.jxr-bar i{position:absolute;top:0;bottom:0;min-width:2px;background:var(--theme-primary-color,#00a4dc)}',
    '.jxr-tot{flex:0 0 4.5em;text-align:right;font-size:.85em;opacity:.8}',
    '.jxr-axis{flex:1;display:flex;justify-content:space-between;font-size:.75em;opacity:.55}',
    '.jxr-axis-row{margin-top:.1em}.jxr-axis-row .jxr-face{height:0;background:none}',
    '.jxr-more{margin-top:.6em}',
    '@media (max-width:40em){.jxr-who{flex-basis:7em}.jxr-tot{flex-basis:3.5em}}'
  ].join('');

  function css() {
    if (document.getElementById('jxr-style')) { return; }
    var s = document.createElement('style');
    s.id = 'jxr-style';
    s.textContent = STYLE;
    document.head.appendChild(s);
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function clock(sec) {
    sec = Math.max(0, Math.round(sec));
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    return (h ? h + ':' + (m < 10 ? '0' : '') : '') + m + ':' + (s < 10 ? '0' : '') + s;
  }

  function total(sec) {
    return sec < 60 ? sec + ' s' : Math.round(sec / 60) + ' min';
  }

  function face(a) {
    if (!a.PersonId || !a.HasImage || !window.ApiClient) { return ''; }
    return ' style="background-image:url(\'' + ApiClient.getUrl('Items/' + a.PersonId + '/Images/Primary', { maxHeight: 96, quality: 90 }) + '\')"';
  }

  /** Renders a CastTimelineView into el. opts.limit = rows before "Show all" (0 = all). */
  function render(el, view, opts) {
    css();
    opts = opts || {};
    var dur = Math.max(1, view.DurationSeconds || 1);
    var limit = opts.limit || 0;
    var actors = view.Actors || [];

    function rows(all) {
      return actors.slice(0, all || !limit ? actors.length : limit).map(function (a) {
        var spans = (a.Spans || []).map(function (s) {
          return '<i style="left:' + (s.Start / dur * 100).toFixed(3) + '%;width:' + ((s.End - s.Start) / dur * 100).toFixed(3) + '%" title="' +
            clock(s.Start) + ' – ' + clock(s.End) + '"></i>';
        }).join('');
        return '<div class="jxr-row"><div class="jxr-face"' + face(a) + '></div>' +
          '<div class="jxr-who"><b title="' + esc(a.Name) + '">' + esc(a.Name) + '</b>' + (a.Character ? '<span title="' + esc(a.Character) + '">' + esc(a.Character) + '</span>' : '') + '</div>' +
          '<div class="jxr-bar">' + spans + '</div>' +
          '<div class="jxr-tot" title="' + (a.Spans || []).length + ' appearance(s)">' + total(a.TotalSeconds) + '</div></div>';
      }).join('');
    }

    function draw(all) {
      var axis = [0, .25, .5, .75, 1].map(function (f) { return '<span>' + clock(dur * f) + '</span>'; }).join('');
      el.innerHTML = '<div class="jxr">' +
        (opts.title === false ? '' : '<h2 class="sectionTitle">X-Ray: cast timeline</h2>') +
        '<div class="jxr-sub">When each actor is on screen. Source: ' + (view.Source === 'Xrayarr' ? 'Xrayarr' : 'face scan') + '. Hover a bar for times.</div>' +
        (actors.length ? rows(all) + '<div class="jxr-row jxr-axis-row"><div class="jxr-face"></div><div class="jxr-who"></div><div class="jxr-axis">' + axis + '</div><div class="jxr-tot"></div></div>' : '<p><em>No cast member recognized in this video.</em></p>') +
        (limit && actors.length > limit && !all ? '<button is="emby-button" type="button" class="raised jxr-more"><span>Show all ' + actors.length + '</span></button>' : '') +
        '</div>';
      var more = el.querySelector('.jxr-more');
      if (more) { more.addEventListener('click', function () { draw(true); }); }
    }

    draw(false);
  }

  function fetchView(id) {
    return ApiClient.getJSON(ApiClient.getUrl('JellyXR/Timeline/' + id));
  }

  window.JellyXR = { render: render, fetch: fetchView, clock: clock };

  // ---- item details page ----
  var pending = {};

  function currentId() {
    if (!/details/i.test(location.href)) { return null; }
    var m = /[?&]id=([0-9a-f]{32}|[0-9a-f-]{36})/i.exec(location.href);
    return m ? m[1].replace(/-/g, '').toLowerCase() : null;
  }

  function visiblePage() {
    var pages = document.querySelectorAll('.itemDetailPage');
    for (var i = 0; i < pages.length; i++) {
      if (!pages[i].classList.contains('hide')) { return pages[i]; }
    }
    return null;
  }

  function place(page, box, tries) {
    // under the overview, above the cast row; pages fill in asynchronously, so wait a bit for the anchor
    var anchor = page.querySelector('#castCollapsible, .peopleSection, #guestCastCollapsible');
    if (anchor && anchor.parentNode) {
      anchor.parentNode.insertBefore(box, anchor);
      return;
    }
    if (tries > 0) {
      setTimeout(function () { place(page, box, tries - 1); }, 400);
      return;
    }
    var content = page.querySelector('.detailPageContent') || page;
    content.appendChild(box);
  }

  function onView() {
    var id = currentId();
    if (!id || !window.ApiClient || pending[id]) { return; }
    var page = visiblePage();
    if (!page) { return; }
    var old = page.querySelectorAll('.jxr-host');
    for (var i = 0; i < old.length; i++) {
      if (old[i].getAttribute('data-id') === id) { return; }
      old[i].parentNode.removeChild(old[i]);
    }
    pending[id] = true;
    fetchView(id).then(function (view) {
      if (currentId() !== id || !view) { return; }
      var box = document.createElement('div');
      box.className = 'jxr-host verticalSection';
      box.setAttribute('data-id', id);
      render(box, view, { limit: 8 });
      place(page, box, 10);
    }, function () { /* 404: no scene data for this item */ }).then(function () { delete pending[id]; });
  }

  // ---- pause panel over the web video player ----
  var OV_STYLE = [
    '.jxr-ov{position:fixed;top:5em;right:1.5em;width:min(24em,calc(100vw - 3em));max-height:calc(100vh - 12em);overflow:auto;',
    'z-index:100000;padding:1em 1.1em;border-radius:12px;background:rgba(12,14,18,.84);color:#fff;',
    '-webkit-backdrop-filter:blur(8px);backdrop-filter:blur(8px);box-shadow:0 8px 32px rgba(0,0,0,.45);font-size:.95em;animation:jxr-in .18s ease-out}',
    '@keyframes jxr-in{from{opacity:0;transform:translateX(1em)}to{opacity:1;transform:none}}',
    '.jxr-ov-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:.7em}',
    '.jxr-ov-ctx{margin:-.55em 0 .7em;font-size:.8em;opacity:.65;font-variant-numeric:tabular-nums}',
    '.jxr-ov-head b{font-size:1.05em;letter-spacing:.02em}',
    '.jxr-ov-x{background:none;border:0;color:inherit;font-size:1.4em;line-height:1;cursor:pointer;opacity:.7;padding:0 .2em}',
    '.jxr-ov-x:hover{opacity:1}',
    '.jxr-ov-card{display:flex;gap:.8em;align-items:center;margin:.55em 0}',
    '.jxr-ov-card .jxr-face{width:3.6em;height:3.6em}',
    '.jxr-ov-small .jxr-face{width:2.4em;height:2.4em}',
    '.jxr-ov-t{min-width:0;line-height:1.25}',
    '.jxr-ov-t b,.jxr-ov-t span,.jxr-ov-t em{display:block;overflow:hidden;text-overflow:ellipsis}',
    '.jxr-ov-t span{opacity:.75;font-size:.9em}',
    '.jxr-ov-t em{font-style:normal;opacity:.6;font-size:.8em}',
    '.jxr-ov-sec{margin-top:.9em;padding-top:.6em;border-top:1px solid rgba(255,255,255,.15);font-size:.8em;opacity:.7;text-transform:uppercase;letter-spacing:.06em}'
  ].join('');

  var overlay = null, hideTimer = null, pauseSeq = 0;

  function hideOverlay() {
    pauseSeq++;
    clearTimeout(hideTimer);
    if (overlay && overlay.parentNode) { overlay.parentNode.removeChild(overlay); }
    overlay = null;
  }

  function ovCard(c, small) {
    return '<div class="jxr-ov-card' + (small ? ' jxr-ov-small' : '') + '">' +
      '<div class="jxr-face"' + face({ PersonId: c.PersonId, HasImage: c.HasImage }) + '></div>' +
      '<div class="jxr-ov-t"><b dir="auto">' + esc(c.Actor) + '</b>' +
      (c.Character ? '<span dir="auto">' + esc(c.Character) + '</span>' : '') +
      (c.Info && !small ? '<em>' + esc(c.Info) + '</em>' : '') + '</div></div>';
  }

  function showOverlay(d) {
    css();
    if (!document.getElementById('jxr-ov-style')) {
      var s = document.createElement('style');
      s.id = 'jxr-ov-style';
      s.textContent = OV_STYLE;
      document.head.appendChild(s);
    }
    clearTimeout(hideTimer);
    if (overlay && overlay.parentNode) { overlay.parentNode.removeChild(overlay); }
    var on = d.Cards.filter(function (c) { return c.OnScreen; });
    var rest = d.Cards.filter(function (c) { return !c.OnScreen; });
    var html = '<div class="jxr-ov-head"><b>' + (on.length ? 'On screen' : esc(d.Header || 'Cast')) + '</b>' +
      '<button class="jxr-ov-x" type="button" title="Close">×</button></div>' +
      (d.Context ? '<div class="jxr-ov-ctx" dir="auto">' + esc(d.Context) + '</div>' : '');
    html += (on.length ? on : rest).map(function (c) { return ovCard(c, false); }).join('');
    if (on.length && rest.length) {
      html += '<div class="jxr-ov-sec">Also in cast</div>' + rest.map(function (c) { return ovCard(c, true); }).join('');
    }
    overlay = document.createElement('div');
    overlay.className = 'jxr-ov';
    overlay.innerHTML = html;
    // keep clicks on the panel from toggling the player underneath
    overlay.addEventListener('click', function (e) { e.stopPropagation(); });
    overlay.querySelector('.jxr-ov-x').addEventListener('click', hideOverlay);
    document.body.appendChild(overlay);
    if (d.OverlaySeconds > 0) { hideTimer = setTimeout(hideOverlay, d.OverlaySeconds * 1000); }
  }

  // what this browser is playing, from the server's view of this session
  function nowPlaying() {
    return ApiClient.getSessions({ deviceId: ApiClient.deviceId() }).then(function (list) {
      var s = (list || []).filter(function (x) { return x.NowPlayingItem; })[0];
      return s ? { id: s.NowPlayingItem.Id, ticks: (s.PlayState && s.PlayState.PositionTicks) || 0 } : null;
    });
  }

  function isPlayerVideo(t) { return t && t.tagName === 'VIDEO'; }

  document.addEventListener('pause', function (e) {
    var v = e.target;
    if (!isPlayerVideo(v) || v.ended || !window.ApiClient) { return; }
    var seq = ++pauseSeq;
    // give the player a moment to report the paused position to the server
    setTimeout(function () {
      if (seq !== pauseSeq || !v.paused) { return; }
      nowPlaying().then(function (np) {
        if (!np || seq !== pauseSeq || !v.paused) { return; }
        return ApiClient.getJSON(ApiClient.getUrl('JellyXR/Cards/' + np.id, { positionTicks: np.ticks })).then(function (d) {
          if (d && d.Cards && d.Cards.length && seq === pauseSeq && v.paused) { showOverlay(d); }
        });
      }).catch(function () { /* 204 = nothing to show, or not available */ });
    }, 700);
  }, true);

  ['play', 'playing', 'emptied', 'ended'].forEach(function (ev) {
    document.addEventListener(ev, function (e) { if (isPlayerVideo(e.target)) { hideOverlay(); } }, true);
  });
  window.addEventListener('hashchange', hideOverlay);

  function later() { setTimeout(onView, 250); }
  document.addEventListener('viewshow', later);
  window.addEventListener('hashchange', later);
  window.addEventListener('popstate', later);
  later();
})();
