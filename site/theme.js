// Theme toggle + persistence, shared by the landing page and docs.
(function () {
  var saved = localStorage.getItem("gert-theme");
  var dark = saved === "dark" || (!saved && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
})();

document.addEventListener("DOMContentLoaded", function () {
  var toggle = document.querySelector(".theme-toggle");
  if (toggle) {
    toggle.addEventListener("click", function () {
      var next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-theme", next);
      localStorage.setItem("gert-theme", next);
    });
  }
  var burger = document.querySelector(".sidebar-toggle");
  var sidebar = document.querySelector(".docs-sidebar");
  if (burger && sidebar) {
    burger.addEventListener("click", function () { sidebar.classList.toggle("open"); });
  }
});
