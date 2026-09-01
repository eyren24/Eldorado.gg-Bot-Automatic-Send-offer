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

            // execCommand keeps the site's own input handling intact (React included); the
            // native setter is the fallback for the rest.
            //
            // Line breaks are written one at a time with insertLineBreak, NOT by handing the
            // whole string to insertText: in a contenteditable a "\n" inside inserted text is
            // just whitespace and collapses, which is why a multi-line template used to
            // arrive as one run-on line. Pressing Enter is not an option either — the chat
            // reads that as "send".
            type: function (box, text) {
              box.focus();
              try {
                if (box.isContentEditable) {
                  var sel = window.getSelection(), range = document.createRange();
                  range.selectNodeContents(box); sel.removeAllRanges(); sel.addRange(range);
                } else { box.select(); }
              } catch (err) { }

              var lines = String(text == null ? '' : text).split(/\r\n|\r|\n/);
              var done = true;
              try {
                for (var i = 0; i < lines.length; i++) {
                  if (i > 0) {
                    var broke = false;
                    try { broke = document.execCommand('insertLineBreak'); } catch (e1) { broke = false; }
                    if (!broke) {
                      try { broke = document.execCommand('insertHTML', false, '<br>'); } catch (e2) { broke = false; }
                    }
                    if (!broke) {
                      try { broke = document.execCommand('insertText', false, '\n'); } catch (e3) { broke = false; }
                    }
                    if (!broke) { done = false; }
                  }
                  if (lines[i].length) {
                    var wrote = false;
                    try { wrote = document.execCommand('insertText', false, lines[i]); } catch (e4) { wrote = false; }
                    if (!wrote) { done = false; }
                  }
                }
              } catch (err) { done = false; }

              if (!done || !__el.value(box)) {
                // Whole-value fallback. A textarea keeps "\n" natively; a contenteditable
                // needs real <br> nodes, so the text is rebuilt rather than assigned.
                if (box.isContentEditable) {
                  while (box.firstChild) { box.removeChild(box.firstChild); }
                  for (var j = 0; j < lines.length; j++) {
                    if (j > 0) { box.appendChild(document.createElement('br')); }
                    if (lines[j].length) { box.appendChild(document.createTextNode(lines[j])); }
                  }
                  box.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
                } else {
                  var flat = box.tagName === 'TEXTAREA' ? lines.join('\n') : lines.join(' ');
                  var proto = box.tagName === 'TEXTAREA'
                    ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
                  Object.getOwnPropertyDescriptor(proto, 'value').set.call(box, flat);
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
            // The button that sends the message — never the one that attaches a file.
            //
            // This used to test /send|invia|submit/ against the class name as well, walking
            // the list backwards and taking the first hit. In a composer bar whose wrapper
            // is called something like "sendbox", the attach control matches on class and
            // sits last, so pressing "send" opened a file picker instead. Now the name of
            // the button decides, the class is only a weak hint, and anything that looks
            // like attach/emoji/voice is excluded outright.
            // Words that name a control which is definitely NOT send. Checked against the
            // button's name; only the unmistakable ones are also checked against its class,
            // because class lists are full of layout words ("clip", "close") that would
            // throw away the real button.
            bannedName: /attach|allega|upload|carica|file|photo|foto|image|immagine|picture|gallery|emoji|emoticon|gif|sticker|adesivo|record|audio|voice|micro|camera|cancel|annulla|delete|remove|rimuovi|close|chiudi/,
            bannedClass: /attach|allega|upload|carica|emoji|emoticon|gif|sticker|adesivo/,
            wantedSend: /(^|[^a-z])(send|invia|submit|enviar|senden|envoyer)([^a-z]|$)/,

            /// True while a control cannot be pressed. Covers a real button's own flag, the
            /// ARIA one, and the bare attribute a div playing button carries instead.
            off: function (e) {
              if (e.disabled) { return true; }
              if (__el.attr(e, 'aria-disabled') === 'true') { return true; }
              if (e.hasAttribute && e.hasAttribute('disabled')) { return true; }
              return false;
            },

            // The button that sends the message — never the one that attaches a file.
            //
            // Pass includeDisabled to find it even while the chat has it greyed out, which
            // is what it does during an upload. Telling "not there" apart from "not ready
            // yet" is the whole point: pressing during an upload does nothing, and the old
            // selector answered that state by falling through to the attach control and
            // opening a file picker.
            sendButton: function (box, includeDisabled) {
              var nodes = Array.prototype.slice.call(
                __el.panel(box).querySelectorAll('button,[role="button"],[type="submit"]'));

              var best = null, bestScore = -1;
              for (var i = 0; i < nodes.length; i++) {
                var e = nodes[i];
                if (!__el.visible(e)) { continue; }
                if (!includeDisabled && __el.off(e)) { continue; }

                // A control that owns a file input is the attach button, whatever it is called.
                if (e.querySelector && e.querySelector('input[type="file"]')) { continue; }

                var label = __el.low(__el.attr(e, 'aria-label') + ' ' + __el.attr(e, 'title') + ' ' +
                                     __el.attr(e, 'data-testid') + ' ' + __el.norm(e.textContent));
                var cls = __el.low(__el.cls(e));
                if (__el.bannedName.test(label) || __el.bannedClass.test(cls)) { continue; }

                var rank = -1;
                if (__el.wantedSend.test(label)) { rank = 3; }                      // named send
                else if (__el.wantedSend.test(cls)) { rank = 2; }                   // class only
                else if (__el.low(__el.attr(e, 'type')) === 'submit') { rank = 1; } // a submit control
                if (rank < 0) { continue; }

                // Same rank: the one further down the bar wins, which is where send sits.
                var score = rank * 1000 + i;
                if (score > bestScore) { bestScore = score; best = e; }
              }
              return best;
            },

            /// Readable name of a button, for the log.
            label: function (e) {
              return __el.norm(__el.attr(e, 'aria-label') || __el.attr(e, 'title') ||
                               __el.attr(e, 'data-testid') || e.textContent || __el.cls(e)).slice(0, 40);
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
        (function (name) {
          try {
            var wanted = /chatta|chat|messagg|message|contatta|scrivi/i;

            // The site's own support widget and the footer links read the same to a text
            // match, and pressing one navigates off the request entirely. Excluded outright.
            // "Contattaci" is the footer, not the buyer: the difference between writing to
            // them and writing to Eldorado is one pronoun, so it is spelled out here.
            var banned = /contattaci|contact ?us|support|supporto|assistenz|help|aiuto|faq|about|chi siamo|termini|terms|privacy|cookie|24\/7/i;

            var nodes = Array.prototype.slice.call(
              document.querySelectorAll('button,a,[role="button"],[type="button"]'));
            var best = null, bestLabel = '', bestScore = -1;

            for (var i = 0; i < nodes.length; i++) {
              var e = nodes[i];
              if (!__el.visible(e) || e.disabled) { continue; }

              // The button that opens the buyer's chat belongs to the request itself. What
              // sits in the site chrome is the support widget and the footer links, and
              // pressing one of those navigates off the page entirely.
              if (e.closest && e.closest('footer,header,nav')) { continue; }
              var label = __el.norm(__el.norm(e.textContent) + ' ' + __el.attr(e, 'aria-label') + ' ' +
                                    __el.attr(e, 'title') + ' ' + __el.attr(e, 'data-testid'));
              // A whole card matches too: only short labels are really the button.
              if (!label || label.length > 60) { continue; }
              if (banned.test(label) || !wanted.test(label)) { continue; }

              var score = 0;
              if (name && __el.low(label).indexOf(__el.low(name)) >= 0) { score += 100; }
              if (/^(chatta|chat|messagg|scrivi)/i.test(label)) { score += 20; }
              if (e.tagName === 'BUTTON') { score += 5; }
              score += Math.max(0, 40 - label.length) / 10;

              if (score > bestScore) { bestScore = score; best = e; bestLabel = label; }
            }

            if (!best) { return { ok: false, reason: 'nessun pulsante per aprire la chat in questa pagina' }; }
            __el.click(best);
            return { ok: true, reason: 'premuto "' + bestLabel.slice(0, 40) + '"' };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })(__BUYER__)
        """;

    /// <summary>Puts the caret in the composer, so a real paste lands there and nowhere else.</summary>
    public const string FocusComposer = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }
            box.focus();
            if (box.isContentEditable) {
              var sel = window.getSelection(), range = document.createRange();
              range.selectNodeContents(box); range.collapse(false);
              sel.removeAllRanges(); sel.addRange(range);
            }
            return { ok: document.activeElement === box, reason: box.tagName.toLowerCase() };
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

            // The composer bar shows a preview of what is attached but not yet sent, and
            // counting that would call a file "delivered" while it is still sitting there.
            var bar = box.parentElement || box;
            var found = panel.querySelectorAll('img,picture,canvas,video,[style*="background-image"]');
            var images = 0;
            for (var i = 0; i < found.length; i++) {
              if (!bar.contains(found[i])) { images++; }
            }

            return {
              ok: true,
              images: images,
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

    /// <summary>
    /// Whether the chat is ready to be told to send: is there a send button at all, and is
    /// it pressable yet? A chat greys it out while a file is uploading, so this is what
    /// separates "no button here" from "the upload hasn't finished".
    /// </summary>
    public const string SendState = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }

            var ready = __el.sendButton(box, false);
            var any = __el.sendButton(box, true);

            return {
              ok: true,
              ready: !!ready,
              present: !!any,
              waiting: !!(any && !ready),
              label: any ? __el.label(any) : '',
              text: __el.value(box).length
            };
          } catch (e) { return { ok: false, reason: String(e) }; }
        })()
        """;

    /// <summary>Clicks the chat's send button; skipped when disabled, i.e. nothing to send.</summary>
    public const string ClickSend = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: true, reason: 'casella sparita' }; }
            var btn = __el.sendButton(box, false);
            if (!btn) {
              // Say which of the two it is: a chat whose send button is merely greyed out
              // is still uploading, and that is worth waiting for rather than giving up on.
              var off = __el.sendButton(box, true);
              return off
                ? { ok: false, waiting: true, reason: 'pulsante "' + __el.label(off) + '" ancora disabilitato' }
                : { ok: false, waiting: false, reason: 'pulsante di invio non trovato' };
            }
            __el.click(btn);
            // The name goes in the log: if the wrong control is ever pressed again, the
            // report says which one instead of just "premuto".
            return { ok: true, reason: 'premuto invio: "' + __el.label(btn) + '"' };
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
    /// Whether something is sitting in the composer bar waiting to be sent — the preview a
    /// chat shows for a file you attached but haven't sent yet.
    /// </summary>
    /// <remarks>
    /// This is what tells a paste that worked from one that silently did nothing. Without it
    /// the only answer available is <see cref="PanelState"/>, which can't say anything until
    /// the file has been sent, so a dead paste cost ten seconds of polling before the next
    /// route was even tried.
    /// </remarks>
    public const string Staged = """
        (function () {
          try {
            var box = __el.composer();
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }

            // The composer bar: a few levels up from the box, never the whole panel — the
            // conversation above is full of images that have already been sent, and counting
            // one of those would report an attachment that never left the composer.
            // The parent is measured BEFORE climbing into it, so the walk stops just short
            // of the oversized ancestor instead of landing on it.
            var bar = box;
            for (var i = 0; i < 4 && bar.parentElement; i++) {
              var parent = bar.parentElement;
              if (parent.getBoundingClientRect().height > 220) { break; }
              bar = parent;
            }

            // Deliberately no [class*="attach"] here: that is what the attach *button* is
            // called, and it is part of the furniture. Counting it made the composer look
            // permanently loaded, so a sent attachment still read as pending.
            var nodes = bar.querySelectorAll('img,canvas,video,[style*="background-image"],[class*="preview"],[class*="thumb"]');
            var staged = 0;
            for (var j = 0; j < nodes.length; j++) {
              var n = nodes[j];
              if (!__el.visible(n)) { continue; }

              // Icons live inside the toolbar buttons and never go anywhere. Only content
              // that is not part of a control counts as a file waiting to be sent.
              if (n.closest && n.closest('button,[role="button"],label,[type="submit"]')) { continue; }

              staged++;
            }

            return { ok: true, staged: staged, text: __el.value(box).length };
          } catch (e) { return { ok: false, reason: String(e) }; }
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
