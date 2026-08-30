using System.Text.Json;

namespace EldoradoApp.Services;

/// <summary>
/// The JavaScript the app injects into Eldorado's messages page to drive the chat.
/// </summary>
/// <remarks>
/// The seller API has no messaging endpoint, so the only way to talk to a buyer is to act
/// on the logged-in web session. The markup belongs to a third party (TalkJS) and changes
/// without notice, so nothing here keys off a specific class name: every step is written
/// against what a chat <i>looks like</i> — a conversation list on the left, a composer at
/// the bottom of the open panel, a header naming the person you are writing to.
/// <see cref="Prelude"/> holds those primitives; the bodies below are the individual steps
/// <see cref="ChatBrowserMessenger"/> runs, one script per round trip so the page has time
/// to react in between (an injected script cannot await).
/// <para>Every body evaluates to <c>{ ok: bool, reason: string, … }</c>.</para>
/// </remarks>
public static class ChatScripts
{
    /// <summary>Wraps one step with the helpers and substitutes its arguments.</summary>
    public static string Compose(string body, string? buyer = null, string? text = null, string? imageJson = null)
    {
        var script = Prelude + body + Suffix;

        if (buyer is not null)
        {
            script = script.Replace("__BUYER__", JsonSerializer.Serialize(buyer));
        }

        if (text is not null)
        {
            script = script.Replace("__TEXT__", JsonSerializer.Serialize(text));
        }

        if (imageJson is not null)
        {
            script = script.Replace("__IMAGE__", imageJson);
        }

        return script;
    }

    /// <summary>Shape-based helpers shared by every step. Ends with <c>return (</c>.</summary>
    private const string Prelude = """
        (function () {
          var __el = {
            norm: function (s) { return (s == null ? '' : String(s)).replace(/ /g, ' ').replace(/\s+/g, ' ').trim(); },
            low: function (s) { return __el.norm(s).toLowerCase(); },
            visible: function (e) {
              if (!e || !e.getBoundingClientRect) { return false; }
              var r = e.getBoundingClientRect();
              return r.width > 1 && r.height > 1;
            },
            attr: function (e, name) { return e && e.getAttribute ? (e.getAttribute(name) || '') : ''; },
            cls: function (e) { return typeof e.className === 'string' ? e.className : ''; },
            hint: function (e) {
              return __el.low(__el.attr(e, 'placeholder') + ' ' + __el.attr(e, 'aria-label') + ' ' +
                              __el.attr(e, 'name') + ' ' + __el.attr(e, 'title') + ' ' +
                              (e.type || '') + ' ' + __el.cls(e));
            },
            isSearch: function (e) { return /search|cerca|filtr|buscar|recherche|suchen/.test(__el.hint(e)); },
            editables: function () {
              var nodes = Array.prototype.slice.call(document.querySelectorAll(
                '[contenteditable="true"],[contenteditable=""],textarea,' +
                'input[type="text"],input[type="search"],input:not([type])'));
              return nodes.filter(function (e) { return __el.visible(e) && !e.disabled && !e.readOnly; });
            },

            // The composer is the editable box at the bottom of the open conversation —
            // never the inbox filter, which is why search-looking boxes are dropped first.
            composer: function () {
              var nodes = __el.editables().filter(function (e) { return !__el.isSearch(e); });
              if (!nodes.length) { return null; }
              nodes.sort(function (a, b) { return a.getBoundingClientRect().top - b.getBoundingClientRect().top; });
              return nodes[nodes.length - 1];
            },
            depth: function (e) { var d = 0; while (e && e.parentElement) { d++; e = e.parentElement; } return d; },
            commonDepth: function (a, b) {
              var up = [], e = a;
              while (e) { up.push(e); e = e.parentElement; }
              e = b;
              while (e) { if (up.indexOf(e) >= 0) { return __el.depth(e); } e = e.parentElement; }
              return -1;
            },

            // The inbox filter, not the site-wide search bar that sits above it: of the
            // search-looking boxes, the right one is the one that shares the most of its
            // ancestry with the chat.
            search: function () {
              var nodes = __el.editables().filter(function (e) { return __el.isSearch(e); });
              if (!nodes.length) { return null; }
              var box = __el.composer();
              if (!box) { return nodes[nodes.length - 1]; }
              var best = nodes[0], score = -1;
              for (var i = 0; i < nodes.length; i++) {
                var d = __el.commonDepth(nodes[i], box);
                if (d > score) { score = d; best = nodes[i]; }
              }
              return best;
            },

            leaves: function () {
              var out = [];
              var all = document.body ? document.body.getElementsByTagName('*') : [];
              for (var i = 0; i < all.length; i++) {
                var e = all[i];
                if (e.children.length) { continue; }
                var t = __el.norm(e.textContent);
                if (!t || t.length > 64) { continue; }
                if (!__el.visible(e)) { continue; }
                out.push(e);
              }
              return out;
            },
            named: function (name) {
              var t = __el.low(name);
              if (!t) { return []; }
              return __el.leaves().filter(function (e) { return __el.low(e.textContent) === t; });
            },

            // From the element holding the name, climb to the box that is the list row: of
            // the ancestors wide enough to be one, the row is the one with the most
            // siblings — that is what being an item of a repeated list looks like.
            row: function (leaf) {
              var e = leaf, best = leaf, count = -1;
              for (var i = 0; i < 8 && e; i++) {
                var r = e.getBoundingClientRect();
                if (r.height > 200) { break; }
                var siblings = e.parentElement ? e.parentElement.children.length : 0;
                if (siblings > count && r.width >= 100 && r.height >= 24) { count = siblings; best = e; }
                e = e.parentElement;
              }
              return best;
            },

            // The name can appear in several places (list row, panel header, a quoted
            // message). The list row is the one with the most siblings — a list has many
            // rows, a header has a handful of children — and sits furthest left.
            bestRow: function (name) {
              var hits = __el.named(name), best = null, score = -1e9;
              for (var i = 0; i < hits.length; i++) {
                var row = __el.row(hits[i]);
                var siblings = row.parentElement ? row.parentElement.children.length : 0;
                var s = siblings * 10 - row.getBoundingClientRect().left / 100;
                if (s > score) { score = s; best = { leaf: hits[i], row: row, siblings: siblings }; }
              }
              return best;
            },
            selected: function (e) {
              for (var i = 0; i < 6 && e; i++) {
                if (__el.attr(e, 'aria-selected') === 'true' || __el.attr(e, 'aria-current')) { return true; }
                if (/(^|[-_ ])(selected|active|current|is-open|opened)([-_ ]|$)/i.test(__el.cls(e))) { return true; }
                e = e.parentElement;
              }
              return false;
            },
            firstLeaf: function (e) {
              var all = e.getElementsByTagName('*');
              for (var i = 0; i < all.length; i++) {
                if (all[i].children.length) { continue; }
                var t = __el.norm(all[i].textContent);
                if (t) { return t; }
              }
              return __el.norm(e.textContent).slice(0, 40);
            },

            // Every conversation name in the same list as the given row.
            siblingNames: function (row) {
              var out = [], p = row ? row.parentElement : null;
              if (!p) { return out; }
              for (var i = 0; i < p.children.length; i++) {
                var t = __el.firstLeaf(p.children[i]);
                if (t && out.indexOf(t) < 0) { out.push(t); }
              }
              return out;
            },

            // The panel that owns the composer: climb until the box is big enough to be one.
            panel: function (box) {
              var e = box;
              for (var i = 0; i < 12 && e.parentElement; i++) {
                var r = e.getBoundingClientRect();
                if (r.height > 280 && r.width > 240) { break; }
                e = e.parentElement;
              }
              return e;
            },

            // Text of the band above the open conversation — where the name of the person
            // you are writing to is shown. This is what confirms the right chat is open.
            header: function () {
              var box = __el.composer();
              if (!box) { return ''; }
              var panel = __el.panel(box);
              var top = panel.getBoundingClientRect().top, out = [];
              var all = panel.getElementsByTagName('*');
              for (var i = 0; i < all.length; i++) {
                var e = all[i];
                if (e.children.length || !__el.visible(e)) { continue; }
                if (e.getBoundingClientRect().top - top > 90) { continue; }
                var t = __el.norm(e.textContent);
                if (t && t.length <= 64 && out.indexOf(t) < 0) { out.push(t); }
              }
              return out.join(' | ');
            },

            click: function (e) {
              try { e.scrollIntoView({ block: 'nearest' }); } catch (err) { }
              var r = e.getBoundingClientRect();
              var o = { bubbles: true, cancelable: true, view: window,
                        clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
              var types = ['pointerover', 'pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'];
              for (var i = 0; i < types.length; i++) {
                try {
                  var pointer = types[i].indexOf('pointer') === 0 && window.PointerEvent;
                  e.dispatchEvent(pointer ? new PointerEvent(types[i], o) : new MouseEvent(types[i], o));
                } catch (err) { }
              }
            },
            value: function (box) {
              return box.isContentEditable ? __el.norm(box.innerText || box.textContent) : __el.norm(box.value);
            },

            // execCommand keeps the site's own input handling intact (React included) and
            // preserves the line breaks; the native setter is the fallback for the rest.
            type: function (box, text) {
              box.focus();
              try {
                if (box.isContentEditable) {
                  var sel = window.getSelection(), range = document.createRange();
                  range.selectNodeContents(box); sel.removeAllRanges(); sel.addRange(range);
                } else { box.select(); }
              } catch (err) { }
              var done = false;
              try { done = document.execCommand('insertText', false, text); } catch (err) { done = false; }
              if (!done || !__el.value(box)) {
                if (box.isContentEditable) {
                  box.textContent = text;
                  box.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
                } else {
                  var proto = box.tagName === 'TEXTAREA'
                    ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
                  Object.getOwnPropertyDescriptor(proto, 'value').set.call(box, text);
                  box.dispatchEvent(new Event('input', { bubbles: true }));
                }
              }
              return __el.value(box);
            },
            enter: function (box) {
              var types = ['keydown', 'keypress', 'keyup'];
              for (var i = 0; i < types.length; i++) {
                box.dispatchEvent(new KeyboardEvent(types[i], {
                  key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true
                }));
              }
            },
            sendButton: function (box) {
              var nodes = Array.prototype.slice.call(
                __el.panel(box).querySelectorAll('button,[role="button"],[type="submit"]'));
              for (var i = nodes.length - 1; i >= 0; i--) {
                var e = nodes[i];
                if (!__el.visible(e) || e.disabled || __el.attr(e, 'aria-disabled') === 'true') { continue; }
                var t = __el.low(__el.attr(e, 'aria-label') + ' ' + __el.attr(e, 'title') + ' ' +
                                 __el.attr(e, 'data-testid') + ' ' + __el.cls(e));
                if (/send|invia|submit/.test(t)) { return e; }
              }
              return null;
            },

            describe: function (e) {
              var r = e.getBoundingClientRect();
              return { tag: e.tagName.toLowerCase(), cls: __el.cls(e).slice(0, 160), id: e.id || '',
                       placeholder: __el.attr(e, 'placeholder'), aria: __el.attr(e, 'aria-label'),
                       testid: __el.attr(e, 'data-testid'), role: __el.attr(e, 'role'),
                       rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)],
                       text: __el.norm(e.textContent).slice(0, 60) };
            },
            chain: function (e) {
              var out = [];
              for (var i = 0; i < 12 && e; i++) { out.push(__el.describe(e)); e = e.parentElement; }
              return out;
            }
          };
          return (
        """;

    private const string Suffix = """

        );
        })();
        """;

    /// <summary>What this document offers: a conversation list, a composer, a filter.</summary>
    public const string Probe = """
        (function (name) {
          try {
            var box = __el.composer();
            var found = __el.bestRow(name);
            return {
              ok: true,
              url: location.href,
              hasComposer: !!box,
              hasSearch: !!__el.search(),
              matches: found ? 1 : 0,
              siblings: found ? found.siblings : 0,
              selected: !!(found && __el.selected(found.row)),
              header: __el.header()
            };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__BUYER__)
        """;

    /// <summary>Opens the buyer's conversation by clicking their row in the list.</summary>
    public const string Select = """
        (function (name) {
          try {
            var found = __el.bestRow(name);
            if (!found) { return { ok: false, reason: 'nessuna riga intestata a ' + name }; }
            __el.click(found.leaf);
            return { ok: true, reason: 'riga aperta (' + found.siblings + ' conversazioni in lista)' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__BUYER__)
        """;

    /// <summary>
    /// Presses the site's own "chat with the buyer" button. This is the route that works
    /// right after an offer: the conversation often does not exist yet, so there is no row
    /// to click in the inbox — that button is what creates it.
    /// </summary>
    public const string ClickChat = """
        (function () {
          try {
            var wanted = /chatta|chat|messagg|message|contatta|contact|scrivi/i;
            var nodes = Array.prototype.slice.call(
              document.querySelectorAll('button,a,[role="button"],[type="button"]'));
            for (var i = 0; i < nodes.length; i++) {
              var e = nodes[i];
              if (!__el.visible(e) || e.disabled) { continue; }
              var label = __el.norm(e.textContent) + ' ' + __el.attr(e, 'aria-label') + ' ' +
                          __el.attr(e, 'title') + ' ' + __el.attr(e, 'data-testid');
              // A whole card can match too: only short labels are really the button.
              if (__el.norm(label).length > 60 || !wanted.test(label)) { continue; }
              __el.click(e);
              return { ok: true, reason: 'premuto "' + __el.norm(label).slice(0, 40) + '"' };
            }
            return { ok: false, reason: 'nessun pulsante per aprire la chat in questa pagina' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })()
        """;

    /// <summary>
    /// What the open conversation currently holds. Compared before and after an upload it
    /// is the only honest answer to "did the banner actually go through?" — the events the
    /// attach step fires say the page accepted them, not that the file was sent.
    /// </summary>
    public const string PanelState = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }
            var panel = __el.panel(box);
            return {
              ok: true,
              images: panel.querySelectorAll('img,picture,canvas,video,[style*="background-image"]').length,
              links: panel.querySelectorAll('a[href],[download]').length,
              length: __el.norm(panel.textContent).length
            };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })()
        """;

    /// <summary>Types the buyer's name into the inbox filter (the list is paginated).</summary>
    public const string Filter = """
        (function (name) {
          try {
            var s = __el.search();
            if (!s) { return { ok: false, reason: 'nessun campo di ricerca nella lista' }; }
            __el.type(s, name);
            return { ok: true, reason: 'lista filtrata su ' + name };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__BUYER__)
        """;

    /// <summary>
    /// Who is the open conversation with? <c>match</c> when the header names the buyer or
    /// their row is highlighted, <c>mismatch</c> when it names somebody else from the same
    /// list, <c>unknown</c> when the page gives no usable signal either way.
    /// </summary>
    public const string Verify = """
        (function (name) {
          try {
            var header = __el.header(), low = __el.low(header);
            var found = __el.bestRow(name);
            var selected = !!(found && __el.selected(found.row));
            var mine = !!__el.low(name) && low.indexOf(__el.low(name)) >= 0;
            var other = '';
            if (found) {
              var names = __el.siblingNames(found.row);
              for (var i = 0; i < names.length && !other; i++) {
                if (names[i].length < 3 || __el.low(names[i]) === __el.low(name)) { continue; }
                if (low.indexOf(__el.low(names[i])) >= 0) { other = names[i]; }
              }
            }
            return {
              ok: true,
              state: (mine || selected) ? 'match' : (other ? 'mismatch' : 'unknown'),
              header: header, other: other, selected: selected, hasComposer: !!__el.composer()
            };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__BUYER__)
        """;

    /// <summary>Writes the message into the composer without sending it.</summary>
    public const string Write = """
        (function (text) {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }
            var written = __el.type(box, text);
            return { ok: !!written, reason: written ? box.tagName.toLowerCase() : 'testo non inserito' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__TEXT__)
        """;

    /// <summary>
    /// Presses Enter in the composer. Kept apart from <see cref="ClickSend"/> so a chat that
    /// reacts to both never sends the same message twice.
    /// </summary>
    public const string Submit = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi sparita' }; }
            __el.enter(box);
            return { ok: true, reason: 'Invio premuto' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })()
        """;

    /// <summary>Clicks the chat's send button; skipped when disabled, i.e. nothing to send.</summary>
    public const string ClickSend = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: true, reason: 'casella sparita' }; }
            var btn = __el.sendButton(box);
            if (!btn) { return { ok: false, reason: 'pulsante di invio non trovato' }; }
            __el.click(btn);
            return { ok: true, reason: 'pulsante di invio premuto' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })()
        """;

    /// <summary>How much text is still sitting in the composer — 0 means it went out.</summary>
    public const string Pending = """
        (function () {
          try {
            var box = __el.composer();
            return { ok: true, pending: box ? __el.value(box).length : 0 };
          } catch (e) { return { ok: false, reason: String(e), pending: 0 }; }
        })()
        """;

    /// <summary>
    /// Attaches the banner. Three routes, most reliable first: the chat's own file input, a
    /// synthetic paste, a synthetic drop. Paste and drop only count as accepted when the
    /// page cancels the event, which is what a handler that took the file does.
    /// </summary>
    public const string Attach = """
        (function (img) {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }

            var bin = atob(img.data), bytes = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) { bytes[i] = bin.charCodeAt(i); }
            var dt = new DataTransfer();
            dt.items.add(new File([bytes], img.name, { type: img.type }));
            var panel = __el.panel(box);

            var inputs = Array.prototype.slice.call(document.querySelectorAll('input[type="file"]'));
            for (var j = 0; j < inputs.length; j++) {
              var input = inputs[j];
              if (!panel.contains(input) && inputs.length > 1) { continue; }
              var accept = __el.low(__el.attr(input, 'accept'));
              if (accept && accept.indexOf('image') < 0 && accept.indexOf('*') < 0) { continue; }
              try {
                input.files = dt.files;
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return { ok: true, reason: 'allegata al campo file della chat' };
              } catch (err) { }
            }

            box.focus();
            if (!box.dispatchEvent(new ClipboardEvent('paste',
                { clipboardData: dt, bubbles: true, cancelable: true }))) {
              return { ok: true, reason: 'incollata nella chat' };
            }

            var drag = { dataTransfer: dt, bubbles: true, cancelable: true };
            try {
              panel.dispatchEvent(new DragEvent('dragenter', drag));
              panel.dispatchEvent(new DragEvent('dragover', drag));
            } catch (err) { }
            if (!panel.dispatchEvent(new DragEvent('drop', drag))) {
              return { ok: true, reason: 'trascinata nella chat' };
            }

            return { ok: false, reason: 'la chat non accetta immagini via script' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__IMAGE__)
        """;

    /// <summary>Everything the selectors above depend on, dumped for the diagnostics report.</summary>
    public const string Diagnose = """
        (function (name) {
          var info = { url: location.href, title: document.title };
          try {
            var box = __el.composer();
            info.composer = box ? __el.describe(box) : null;
            info.composerChain = box ? __el.chain(box) : [];
            info.header = __el.header();
            info.sendButton = box && __el.sendButton(box) ? __el.describe(__el.sendButton(box)) : null;
            var s = __el.search();
            info.search = s ? __el.describe(s) : null;
            info.editables = __el.editables().slice(0, 20).map(function (e) { return __el.describe(e); });
            info.fileInputs = Array.prototype.slice.call(document.querySelectorAll('input[type="file"]'))
              .slice(0, 10).map(function (e) { return __el.describe(e); });
            var found = __el.bestRow(name);
            info.match = found ? {
              leaf: __el.describe(found.leaf), row: __el.describe(found.row), siblings: found.siblings,
              selected: __el.selected(found.row), names: __el.siblingNames(found.row).slice(0, 40),
              rowChain: __el.chain(found.row)
            } : null;
          } catch (e) { info.error = String(e); }
          return info;
        })(__BUYER__)
        """;
}
