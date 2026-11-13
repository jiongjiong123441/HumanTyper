// Release dates from GitHub are UTC timestamps. Show them as the visitor's own calendar date
// (their time zone), plus "今天 / 昨天 / N 天前" relative to the visitor's today.
window.HTDate = (function () {
  var fmt;
  try {
    fmt = new Intl.DateTimeFormat('zh-CN', { year: 'numeric', month: 'long', day: 'numeric' });
  } catch (e) {
    fmt = null;
  }

  function dayStart(d) {
    return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  }

  function relative(d) {
    var days = Math.round((dayStart(new Date()) - dayStart(d)) / 86400000);
    if (days === 0) return '今天';
    if (days === 1) return '昨天';
    if (days > 1 && days < 7) return days + ' 天前';
    return '';
  }

  // "2026年11月13日（昨天）"
  function format(iso) {
    var d = new Date(iso);
    if (isNaN(d)) return '';
    var abs = fmt ? fmt.format(d) : d.getFullYear() + '年' + (d.getMonth() + 1) + '月' + d.getDate() + '日';
    var rel = relative(d);
    return rel ? abs + '（' + rel + '）' : abs;
  }

  // "2026-11-13" in the visitor's time zone, for compact lists
  function short(iso) {
    var d = new Date(iso);
    if (isNaN(d)) return '';
    var p = function (n) { return (n < 10 ? '0' : '') + n; };
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate());
  }

  // Full local date and time, for a tooltip
  function full(iso) {
    var d = new Date(iso);
    return isNaN(d) ? '' : d.toLocaleString('zh-CN', { dateStyle: 'long', timeStyle: 'short' });
  }

  return { format: format, short: short, full: full, relative: relative };
})();
