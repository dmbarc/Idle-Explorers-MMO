// The four things a WebGL build cannot do from C#.
//
// == WHY ANY OF THIS IS NEEDED ================================================
//
// An OAuth redirect has to happen in the PAGE. A desktop build can run a loopback
// server and catch the browser coming back; a web build has no such thing -- the page
// itself navigates to Google and a new page load comes back with the code on its URL.
//
// C# in WebGL cannot touch window.location at all, so these four functions are the
// entire bridge. Everything else about the flow -- the PKCE verifier, the URL, the
// exchange -- is shared C# and identical on every platform.
//
// == WHY THE STRINGS ARE ALLOCATED THIS WAY ===================================
//
// A JavaScript string returned to C# has to live in the heap the C# side reads from,
// and it has to be freed by the C# side. _malloc plus stringToUTF8 is the documented
// arrangement; returning a JS string directly hands back a pointer into memory that
// is about to be collected, which reads as random bytes or a crash much later.

mergeInto(LibraryManager.library, {

  // Sends the page to the authorisation URL. Nothing after this call runs: the
  // document is being torn down.
  IdleOAuthNavigate: function (url) {
    window.location.href = UTF8ToString(url);
  },

  // One query parameter off the current URL, or an empty string.
  //
  // The QUERY, not the fragment. PKCE returns ?code=..., which the deliberate choice
  // of that flow buys us -- an implicit-flow fragment never reaches a server and is
  // messier to clear off the address bar.
  IdleOAuthQuery: function (key) {
    var name = UTF8ToString(key);
    var found = new URLSearchParams(window.location.search).get(name) || "";

    var size = lengthBytesUTF8(found) + 1;
    var buffer = _malloc(size);

    stringToUTF8(found, buffer, size);

    return buffer;
  },

  // Where the page is, minus any query or fragment.
  //
  // Used as the redirect target, so it has to be the bare page URL: handing Supabase
  // a redirect that already carries ?code= from a previous attempt produces a URL with
  // two of them, and the allow-list check fails on a value nobody typed.
  IdleOAuthPageUrl: function () {
    var url = window.location.origin + window.location.pathname;

    var size = lengthBytesUTF8(url) + 1;
    var buffer = _malloc(size);

    stringToUTF8(url, buffer, size);

    return buffer;
  },

  // Takes the spent code off the address bar without reloading.
  //
  // replaceState rather than assigning location: assigning would reload the page,
  // which restarts the whole Unity build -- in the middle of finishing a sign-in.
  // The code is single-use and already redeemed; leaving it in the URL puts it in
  // history and in anything the player pastes.
  IdleOAuthClearQuery: function () {
    try {
      window.history.replaceState(
        {}, document.title,
        window.location.origin + window.location.pathname);
    } catch (e) {
      // Some embedding contexts forbid history manipulation. The code is spent, so
      // this is tidiness rather than safety, and failing it must not break sign-in.
    }
  }

});
