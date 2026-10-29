// Table of contents: mark the open page and its row with selectedLine, keep only the page's own row active, so parent rows
// stay unhighlighted and normal weight, and mark links without children as leaf, as they have no expand stub.
// Runs after docfx.js has rendered the sidebar; parent rows stay open through the "in" class docfx.js sets.
$(function () {
  // The open page: the last active link, as docfx.js also marks the links of its parents active
  var anchor = $('#toc a.active').last();
  if (anchor.length > 0) {
    anchor.addClass('selectedLine');
    anchor.parent().addClass('selectedLine');
    anchor.parent().find('.expand-stub').first().addClass('selectedLine');
    anchor.parents('li').removeClass('active expanded');
    anchor.parent().addClass('active');
  }

  // Links without children
  $('#toc li > a').filter(function () {
    return $(this).siblings().length === 0;
  }).addClass('leaf');
});

// Code snippets: a copy button at the top right corner of each, shown while the mouse is over the snippet or the button
// has the keyboard focus. The button is in a wrapper beside the <pre>, so it stays put when a wide snippet scrolls.
$(function () {
  $('article pre').each(function () {
    var pre = $(this);
    var button = $('<button type="button" class="copy-code" title="Copy to clipboard">Copy</button>');
    pre.wrap('<div class="code-block"></div>').after(button);
    button.on('click', function () {
      // The code's text without the button; the Clipboard API where the page may use it, else a hidden text area
      var text = (pre.find('code')[0] || pre[0]).innerText.replace(/\n$/, '');
      var copied = navigator.clipboard && window.isSecureContext ? navigator.clipboard.writeText(text) : copyWithTextArea(text);
      Promise.resolve(copied).then(function () { flash(button, 'Copied'); }, function () { flash(button, 'Failed'); });
    });
  });

  // Copy through a selected, off-screen text area, for pages served over plain HTTP
  function copyWithTextArea(text) {
    var area = $('<textarea readonly class="copy-code-buffer"></textarea>').val(text).appendTo('body');
    area[0].select();
    var ok = document.execCommand('copy');
    area.remove();
    return ok ? Promise.resolve() : Promise.reject();
  }

  // Show the result on the button for a moment
  function flash(button, label) {
    button.text(label).addClass('done');
    setTimeout(function () { button.text('Copy').removeClass('done'); }, 1500);
  }
});
