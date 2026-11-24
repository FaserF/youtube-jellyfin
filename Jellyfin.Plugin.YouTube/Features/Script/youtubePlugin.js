(function () {
    "use strict";
    console.log("[YouTube & YouTube Music SmartTube Plugin] Client script active");

    function injectTopSearchButton() {
        var headerRight = document.querySelector(".headerRight");
        if (!headerRight || document.getElementById("ytSmartTubeBtn")) return;

        var btn = document.createElement("button");
        btn.id = "ytSmartTubeBtn";
        btn.type = "button";
        btn.className = "paper-icon-button-light";
        btn.title = "YouTube & YouTube Music Hub — Search, Audio & Account";
        btn.style.cssText = "color:#ff3b30;font-weight:bold;font-size:13px;padding:4px 10px;border:1px solid #ff3b30;border-radius:6px;margin-right:8px;cursor:pointer;background:rgba(229,9,20,0.12);";
        btn.textContent = "▶ YT / 🎵 Music";
        btn.onclick = openYouTubeSearchModal;
        headerRight.insertBefore(btn, headerRight.firstChild);
    }

    function authFetch(url, options) {
        options = options || {};
        var headers = new Headers(options.headers || {});
        var token = window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken();
        if (token) {
            headers.set("X-Emby-Token", token);
        }
        options.headers = headers;
        return fetch(url, options);
    }

    function safeHttpUrl(rawUrl) {
        if (!rawUrl || typeof rawUrl !== "string") return "";
        try {
            var parsed = new URL(rawUrl, window.location.origin);
            if (parsed.protocol === "https:" || parsed.protocol === "http:") {
                return parsed.href;
            }
        } catch (e) {}
        return "";
    }

    function openYouTubeSearchModal() {
        var existing = document.getElementById("ytSmartTubeModal");
        if (existing) existing.remove();

        var pollTimer = null;
        var modal = document.createElement("div");
        modal.id = "ytSmartTubeModal";
        modal.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.90);z-index:99999;display:flex;flex-direction:column;padding:24px;overflow-y:auto;color:#fff;font-family:sans-serif;";
        modal.innerHTML =
            '<div style="max-width:1040px;width:100%;margin:0 auto;">' +
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px;flex-wrap:wrap;gap:10px;">' +
            '<h2 style="margin:0;color:#ff3b30;">▶ YouTube &amp; 🎵 YouTube Music Hub</h2>' +
            '<div style="display:flex;gap:8px;align-items:center;">' +
            '<button id="ytBtnLinkAccount" style="background:#27272a;color:#fff;border:1px solid #444;padding:8px 14px;border-radius:6px;cursor:pointer;font-size:13px;">🔑 My YouTube Account</button>' +
            '<button id="ytModalClose" style="background:#333;color:#fff;border:none;padding:8px 16px;border-radius:6px;cursor:pointer;">✕ Close</button>' +
            '</div></div>' +
            '<div id="ytAccountPanel" style="display:none;background:#18181b;border:1px solid #e50914;border-radius:8px;padding:14px;margin-bottom:16px;">' +
            '<div id="ytAccountInfo" style="font-size:14px;margin-bottom:10px;">Checking account status...</div>' +
            '<button id="ytStartDeviceAuth" style="background:#e50914;color:#fff;border:none;padding:8px 16px;border-radius:6px;font-weight:bold;cursor:pointer;">🔑 Link / Switch Personal YouTube &amp; YT Music Account (TV Code)</button>' +
            '<div id="ytDeviceCodeArea" style="display:none;margin-top:12px;padding:12px;background:#111;border-radius:6px;">' +
            '<div style="font-size:13px;color:#ccc;">Open <a href="https://www.google.com/device" target="_blank" style="color:#ff4d4d;font-weight:bold;">https://www.google.com/device</a> and enter code:</div>' +
            '<div id="ytUserCodeDisplay" style="font-size:22px;font-weight:bold;letter-spacing:3px;margin:8px 0;color:#fff;">---</div>' +
            '<div id="ytDevicePollStatus" style="font-size:12px;color:#aaa;">Waiting for activation...</div>' +
            '</div></div>' +
            '<div style="display:flex;gap:10px;margin-bottom:16px;flex-wrap:wrap;">' +
            '<select id="ytSearchMode" style="padding:10px 12px;border-radius:6px;border:1px solid #444;background:#18181b;color:#fff;font-size:14px;">' +
            '<option value="video">🎬 YouTube Video</option>' +
            '<option value="music">🎵 YouTube Music (Audio)</option>' +
            '</select>' +
            '<input id="ytSearchInput" type="text" placeholder="Search YouTube or YouTube Music (Ad-Free + SponsorBlock)..." style="flex:1;min-width:220px;padding:10px 14px;border-radius:6px;border:1px solid #444;background:#18181b;color:#fff;font-size:15px;" />' +
            '<button id="ytSearchGo" style="background:#e50914;color:#fff;border:none;padding:10px 20px;border-radius:6px;font-weight:bold;cursor:pointer;">Search</button>' +
            '</div>' +
            '<div id="ytAudioPlayerBar" style="display:none;background:#18181b;border:1px solid #333;border-radius:8px;padding:12px;margin-bottom:16px;align-items:center;gap:12px;">' +
            '<div id="ytAudioNowPlaying" style="font-weight:bold;font-size:14px;flex:1;">🎵 Playing YouTube Music...</div>' +
            '<audio id="ytAudioElement" controls autoplay style="height:36px;width:320px;"></audio>' +
            '</div>' +
            '<div id="ytSearchResults" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:16px;"></div>' +
            '</div>';

        document.body.appendChild(modal);
        document.getElementById("ytModalClose").onclick = function () {
            if (pollTimer) clearInterval(pollTimer);
            modal.remove();
        };

        var uid = (window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()) || "";

        document.getElementById("ytBtnLinkAccount").onclick = async function () {
            var panel = document.getElementById("ytAccountPanel");
            panel.style.display = panel.style.display === "none" ? "block" : "none";
            try {
                var r = await authFetch("/api/youtubePlugin/users");
                var d = await r.json();
                var me = (d.users || []).find(function (u) {
                    return (u.id && u.id.replace(/-/g, "") === uid.replace(/-/g, "")) || u.username.toLowerCase() === uid.toLowerCase();
                });
                var infoEl = document.getElementById("ytAccountInfo");
                var labelPrefix = document.createTextNode("👤 Jellyfin User: ");
                var strongUser = document.createElement("strong");
                strongUser.textContent = me ? me.username : uid;
                var sep = document.createTextNode(" — ");
                var statusSpan = document.createElement("span");
                if (me && me.isLinked) {
                    statusSpan.style.color = "#4cd964";
                    statusSpan.textContent = "✅ Linked to " + (me.linkedAccount || "") + " (YouTube & YouTube Music)";
                } else {
                    statusSpan.style.color = "#aaa";
                    statusSpan.textContent = "🌐 Public Mode (Not linked yet)";
                }
                infoEl.replaceChildren(labelPrefix, strongUser, sep, statusSpan);
            } catch (e) {}
        };

        document.getElementById("ytStartDeviceAuth").onclick = async function () {
            if (pollTimer) clearInterval(pollTimer);
            var r = await authFetch("/api/youtubePlugin/auth/device-code", { method: "POST" });
            var data = await r.json();
            document.getElementById("ytDeviceCodeArea").style.display = "block";
            document.getElementById("ytUserCodeDisplay").textContent = data.user_code || "ERROR";
            document.getElementById("ytDevicePollStatus").textContent = "Waiting for confirmation on google.com/device...";

            var ur = await authFetch("/api/youtubePlugin/users");
            var ud = await ur.json();
            var me = (ud.users || []).find(function (u) {
                return (u.id && u.id.replace(/-/g, "") === uid.replace(/-/g, "")) || u.username.toLowerCase() === uid.toLowerCase();
            });
            var jfName = me ? me.username : (uid || "");
            var jfId = me ? me.id : uid;

            if (data.device_code) {
                pollTimer = setInterval(async function () {
                    try {
                        var pr = await authFetch("/api/youtubePlugin/auth/poll?deviceCode=" + encodeURIComponent(data.device_code) + "&jellyfinUser=" + encodeURIComponent(jfName) + "&jellyfinUserId=" + encodeURIComponent(jfId), { method: "POST" });
                        var pd = await pr.json();
                        if (pd.success) {
                            clearInterval(pollTimer);
                            pollTimer = null;
                            document.getElementById("ytDevicePollStatus").style.color = "#4cd964";
                            document.getElementById("ytDevicePollStatus").textContent = "✅ Linked to " + pd.accountName + "!";
                        } else if (pd.pending === false) {
                            clearInterval(pollTimer);
                            pollTimer = null;
                            document.getElementById("ytDevicePollStatus").style.color = "#ff4d4d";
                            document.getElementById("ytDevicePollStatus").textContent = "❌ Authorization failed: " + (pd.error || "Unknown error");
                        }
                    } catch (e) {}
                }, (data.interval || 5) * 1000);
            }
        };

        async function runSearch() {
            var q = document.getElementById("ytSearchInput").value.trim();
            if (!q) return;
            var mode = document.getElementById("ytSearchMode").value;
            var searchQuery = mode === "music" ? q + " Official Audio" : q;
            var resContainer = document.getElementById("ytSearchResults");
            var searchingEl = document.createElement("div");
            searchingEl.textContent = "Searching " + (mode === "music" ? "YouTube Music" : "YouTube") + "...";
            resContainer.replaceChildren(searchingEl);
            try {
                var r = await authFetch("/api/youtubePlugin/search?q=" + encodeURIComponent(searchQuery) + "&userId=" + encodeURIComponent(uid));
                var resData = await r.json();
                var items = Array.isArray(resData) ? resData : (resData.items || []);
                var cards = (items || []).map(function (v) {
                    var vid = String(v.VideoId || "");
                    var titleText = String(v.Title || "");
                    var channelText = String(v.ChannelTitle || "");
                    var safeThumb = safeHttpUrl(v.ThumbnailUrl);

                    var card = document.createElement("div");
                    card.style.cssText = "background:#1c1c1e;border-radius:8px;overflow:hidden;border:1px solid #2c2c2e;display:flex;flex-direction:column;";

                    var img = document.createElement("img");
                    if (safeThumb) {
                        img.setAttribute("src", safeThumb);
                    }
                    img.setAttribute("alt", titleText);
                    img.style.cssText = "width:100%;aspect-ratio:16/9;object-fit:cover;";
                    card.appendChild(img);

                    var body = document.createElement("div");
                    body.style.cssText = "padding:10px;flex:1;display:flex;flex-direction:column;justify-content:space-between;";

                    var metaWrap = document.createElement("div");
                    var titleDiv = document.createElement("div");
                    titleDiv.style.cssText = "font-weight:bold;font-size:14px;margin-bottom:4px;";
                    titleDiv.textContent = titleText;
                    var channelDiv = document.createElement("div");
                    channelDiv.style.cssText = "font-size:12px;color:#aaa;";
                    channelDiv.textContent = channelText;
                    metaWrap.appendChild(titleDiv);
                    metaWrap.appendChild(channelDiv);
                    body.appendChild(metaWrap);

                    var actions = document.createElement("div");
                    actions.style.cssText = "margin-top:10px;display:flex;gap:6px;flex-wrap:wrap;";

                    var playLink = document.createElement("a");
                    playLink.setAttribute("href", "/api/youtubePlugin/stream/" + encodeURIComponent(vid) + "/play");
                    playLink.setAttribute("target", "_blank");
                    playLink.setAttribute("rel", "noopener noreferrer");
                    playLink.style.cssText = "background:#e50914;color:#fff;text-decoration:none;padding:6px 10px;border-radius:4px;font-size:12px;font-weight:bold;";
                    playLink.textContent = "▶ Video";
                    actions.appendChild(playLink);

                    var audioBtn = document.createElement("button");
                    audioBtn.type = "button";
                    audioBtn.style.cssText = "background:#9333ea;color:#fff;border:none;padding:6px 10px;border-radius:4px;font-size:12px;font-weight:bold;cursor:pointer;";
                    audioBtn.textContent = "🎵 Play Music";
                    audioBtn.onclick = function () {
                        var bar = document.getElementById("ytAudioPlayerBar");
                        var audioEl = document.getElementById("ytAudioElement");
                        bar.style.display = "flex";
                        document.getElementById("ytAudioNowPlaying").textContent = "🎵 " + titleText;
                        audioEl.src = "/api/youtubePlugin/stream/" + encodeURIComponent(vid) + "/audio.m4a";
                        audioEl.play();
                    };
                    actions.appendChild(audioBtn);

                    var likeBtn = document.createElement("button");
                    likeBtn.type = "button";
                    likeBtn.style.cssText = "background:#2c2c2e;color:#fff;border:none;padding:6px 10px;border-radius:4px;font-size:12px;cursor:pointer;";
                    likeBtn.textContent = "👍 Like";
                    likeBtn.onclick = async function () {
                        await authFetch("/api/youtubePlugin/rate/" + encodeURIComponent(vid) + "?rating=like&userId=" + encodeURIComponent(uid), { method: "POST" });
                        likeBtn.textContent = "❤️ Liked";
                    };
                    actions.appendChild(likeBtn);

                    body.appendChild(actions);
                    card.appendChild(body);
                    return card;
                });
                resContainer.replaceChildren.apply(resContainer, cards);
            } catch (e) {
                var errEl = document.createElement("div");
                errEl.textContent = "Search error: " + (e && e.message ? e.message : "Unknown error");
                resContainer.replaceChildren(errEl);
            }
        }

        document.getElementById("ytSearchGo").onclick = runSearch;
        document.getElementById("ytSearchInput").onkeydown = function (e) { if (e.key === "Enter") runSearch(); };
    }

    setInterval(injectTopSearchButton, 2500);
})();
