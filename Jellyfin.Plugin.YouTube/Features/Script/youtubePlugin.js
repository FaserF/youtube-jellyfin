(function () {
    "use strict";
    console.log("[YouTube SmartTube Plugin] Client script active");

    function injectTopSearchButton() {
        var headerRight = document.querySelector(".headerRight");
        if (!headerRight || document.getElementById("ytSmartTubeBtn")) return;

        var btn = document.createElement("button");
        btn.id = "ytSmartTubeBtn";
        btn.type = "button";
        btn.className = "paper-icon-button-light";
        btn.title = "YouTube SmartTube Search & Account";
        btn.style.cssText = "color:#ff3b30;font-weight:bold;font-size:13px;padding:4px 10px;border:1px solid #ff3b30;border-radius:6px;margin-right:8px;cursor:pointer;background:rgba(229,9,20,0.12);";
        btn.textContent = "▶ YouTube";
        btn.onclick = openYouTubeSearchModal;
        headerRight.insertBefore(btn, headerRight.firstChild);
    }

    function openYouTubeSearchModal() {
        var existing = document.getElementById("ytSmartTubeModal");
        if (existing) existing.remove();

        var modal = document.createElement("div");
        modal.id = "ytSmartTubeModal";
        modal.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.88);z-index:99999;display:flex;flex-direction:column;padding:24px;overflow-y:auto;color:#fff;font-family:sans-serif;";
        modal.innerHTML =
            '<div style="max-width:1000px;width:100%;margin:0 auto;">' +
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px;">' +
            '<h2 style="margin:0;color:#ff3b30;">▶ YouTube SmartTube Hub</h2>' +
            '<button id="ytModalClose" style="background:#333;color:#fff;border:none;padding:8px 16px;border-radius:6px;cursor:pointer;">✕ Close</button>' +
            '</div>' +
            '<div style="display:flex;gap:10px;margin-bottom:20px;">' +
            '<input id="ytSearchInput" type="text" placeholder="Search YouTube videos (Ad-Free + SponsorBlock)..." style="flex:1;padding:10px 14px;border-radius:6px;border:1px solid #444;background:#18181b;color:#fff;font-size:15px;" />' +
            '<button id="ytSearchGo" style="background:#e50914;color:#fff;border:none;padding:10px 20px;border-radius:6px;font-weight:bold;cursor:pointer;">Search</button>' +
            '</div>' +
            '<div id="ytSearchResults" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:16px;"></div>' +
            '</div>';

        document.body.appendChild(modal);
        document.getElementById("ytModalClose").onclick = function () { modal.remove(); };

        async function runSearch() {
            var q = document.getElementById("ytSearchInput").value.trim();
            if (!q) return;
            var resContainer = document.getElementById("ytSearchResults");
            resContainer.innerHTML = "<div>Searching YouTube...</div>";
            try {
                var uid = (window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()) || "fabian";
                var r = await fetch("/api/youtubePlugin/search?q=" + encodeURIComponent(q) + "&userId=" + encodeURIComponent(uid));
                var items = await r.json();
                resContainer.innerHTML = (items || []).map(function (v) {
                    return '<div style="background:#1c1c1e;border-radius:8px;overflow:hidden;border:1px solid #2c2c2e;display:flex;flex-direction:column;">' +
                        '<img src="' + v.ThumbnailUrl + '" style="width:100%;aspect-ratio:16/9;object-fit:cover;" />' +
                        '<div style="padding:10px;flex:1;display:flex;flex-direction:column;justify-content:space-between;">' +
                        '<div><div style="font-weight:bold;font-size:14px;margin-bottom:4px;">' + v.Title + '</div>' +
                        '<div style="font-size:12px;color:#aaa;">' + (v.ChannelTitle || "") + '</div></div>' +
                        '<div style="margin-top:10px;display:flex;gap:8px;">' +
                        '<a href="/api/youtubePlugin/stream/' + v.VideoId + '/play" target="_blank" style="background:#e50914;color:#fff;text-decoration:none;padding:6px 12px;border-radius:4px;font-size:12px;font-weight:bold;">▶ Stream Ad-Free</a>' +
                        '<button data-like="' + v.VideoId + '" style="background:#2c2c2e;color:#fff;border:none;padding:6px 10px;border-radius:4px;font-size:12px;cursor:pointer;">👍 Like</button>' +
                        '</div></div></div>';
                }).join("");

                resContainer.querySelectorAll("button[data-like]").forEach(function (b) {
                    b.onclick = async function () {
                        var vid = b.getAttribute("data-like");
                        await fetch("/api/youtubePlugin/rate/" + encodeURIComponent(vid) + "?rating=like&userId=" + encodeURIComponent(uid), { method: "POST" });
                        b.textContent = "❤️ Liked";
                    };
                });
            } catch (e) {
                resContainer.innerHTML = "<div>Search error: " + e.message + "</div>";
            }
        }

        document.getElementById("ytSearchGo").onclick = runSearch;
        document.getElementById("ytSearchInput").onkeydown = function (e) { if (e.key === "Enter") runSearch(); };
    }

    setInterval(injectTopSearchButton, 2500);
})();
