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
