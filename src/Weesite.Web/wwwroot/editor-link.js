// The header's link to the admin, shown only in a browser an editor signed in with (the
// weesite.editor cookie, EditorMarker.cs). The page itself is the same for everyone, so the output
// cache keeps one copy; without JavaScript the link stays hidden.
if (document.cookie.split(";").some((part) => part.trim().startsWith("weesite.editor="))) {
  for (const link of document.querySelectorAll("a.editor-link[hidden]")) {
    link.hidden = false;
  }
}
