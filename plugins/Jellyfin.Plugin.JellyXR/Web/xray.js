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

  function later() { setTimeout(onView, 250); }
  document.addEventListener('viewshow', later);
  window.addEventListener('hashchange', later);
  window.addEventListener('popstate', later);
  later();
})();
