/* SafePath BD — community trust votes and report discussion.
   All writes go through the JSON API with the antiforgery token in a header. */
(function () {
    "use strict";

    const MAX_COMMENT = 1500;

    function token() {
        const input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    async function send(url, method, body) {
        const options = {
            method: method,
            headers: {
                "Accept": "application/json",
                "X-CSRF-TOKEN": token()
            },
            credentials: "same-origin"
        };

        if (body !== undefined) {
            options.headers["Content-Type"] = "application/json";
            options.body = JSON.stringify(body);
        }

        const response = await fetch(url, options);
        let payload = null;

        try {
            payload = await response.json();
        } catch (e) {
            payload = null;
        }

        if (!response.ok || !payload || payload.success === false) {
            const error = new Error((payload && payload.message) || "The request could not be completed.");
            error.status = response.status;
            throw error;
        }

        return payload.data;
    }

    function toast(kind, message) {
        if (window.SafePathToast) {
            window.SafePathToast[kind](message);
        }
    }

    /* Counts only animate when the value actually changed. */
    function setCount(el, value) {
        if (!el || el.textContent === String(value)) {
            return;
        }

        el.textContent = String(value);
        el.classList.remove("is-bumped");
        void el.offsetWidth;
        el.classList.add("is-bumped");
    }

    // -------------------------------------------------------------------- votes

    function initVotes() {
        const panel = document.querySelector("[data-trust-panel]");
        if (!panel) {
            return;
        }

        const reportId = panel.getAttribute("data-report-id");
        const buttons = Array.from(panel.querySelectorAll("[data-vote]"));
        const confirmCount = panel.querySelector("[data-confirm-count]");
        const disputeCount = panel.querySelector("[data-dispute-count]");
        const consensus = panel.querySelector("[data-trust-consensus]");
        const note = panel.querySelector("[data-trust-note]");

        function render(summary) {
            setCount(confirmCount, summary.confirmCount);
            setCount(disputeCount, summary.disputeCount);

            if (consensus) {
                consensus.textContent = summary.consensusLabel;
            }

            buttons.forEach(function (button) {
                const active = button.getAttribute("data-vote") === summary.currentUserVote;
                button.classList.toggle("is-active", active);
                button.setAttribute("aria-pressed", String(active));
            });

            if (note) {
                note.textContent = summary.currentUserVote
                    ? "Your response is recorded. Select it again to withdraw it."
                    : "Community feedback helps moderators prioritise. It does not verify a report.";
            }
        }

        buttons.forEach(function (button) {
            button.addEventListener("click", async function () {
                if (button.disabled) {
                    return;
                }

                buttons.forEach(function (b) { b.disabled = true; });
                panel.classList.add("is-busy");

                try {
                    const summary = await send("/api/v1/reports/" + reportId + "/vote", "POST", {
                        voteType: button.getAttribute("data-vote")
                    });
                    render(summary);
                } catch (error) {
                    toast("error", error.message);
                } finally {
                    buttons.forEach(function (b) { b.disabled = false; });
                    panel.classList.remove("is-busy");
                }
            });
        });
    }

    // ----------------------------------------------------------------- comments

    function initComments() {
        const panel = document.querySelector("[data-comments-panel]");
        if (!panel) {
            return;
        }

        const reportId = panel.getAttribute("data-report-id");
        const list = panel.querySelector("[data-comment-list]");
        const form = panel.querySelector("[data-comment-form]");
        const input = panel.querySelector("[data-comment-input]");
        const submit = panel.querySelector("[data-comment-submit]");
        const counter = panel.querySelector("[data-comment-remaining]");
        const countLabel = panel.querySelector("[data-comment-count]");
        const empty = panel.querySelector("[data-comment-empty]");
        const more = panel.querySelector("[data-comment-more]");

        let replyTo = null;

        function escapeHtml(value) {
            const div = document.createElement("div");
            div.textContent = value == null ? "" : String(value);
            return div.innerHTML;
        }

        function bumpCount(delta) {
            if (!countLabel) {
                return;
            }

            const next = Math.max(0, (parseInt(countLabel.textContent, 10) || 0) + delta);
            countLabel.textContent = String(next);

            if (empty) {
                empty.hidden = next > 0;
            }
        }

        /* Built from DOM nodes and textContent, so comment text can never become markup. */
        function buildComment(comment, isReply) {
            const li = document.createElement("li");
            li.className = "comment" + (isReply ? " comment--reply" : "") + " is-entering";
            li.setAttribute("data-comment-id", comment.commentId);

            const avatar = document.createElement("span");
            avatar.className = "comment-avatar";
            avatar.setAttribute("aria-hidden", "true");
            avatar.textContent = comment.authorInitials;

            const body = document.createElement("div");
            body.className = "comment-body";

            const head = document.createElement("div");
            head.className = "comment-head";

            const name = document.createElement("strong");
            name.textContent = comment.authorName;
            head.appendChild(name);

            if (comment.authorIsStaff) {
                const badge = document.createElement("span");
                badge.className = "chip chip--accent chip--plain";
                badge.textContent = "Moderator";
                head.appendChild(badge);
            }

            const time = document.createElement("time");
            time.dateTime = comment.createdAt;
            time.textContent = new Date(comment.createdAt).toLocaleString(undefined, {
                day: "numeric", month: "short", hour: "2-digit", minute: "2-digit"
            });
            head.appendChild(time);

            const text = document.createElement("p");
            text.className = "comment-text";
            text.textContent = comment.text;

            body.appendChild(head);
            body.appendChild(text);

            const actions = document.createElement("div");
            actions.className = "comment-actions";

            if (!isReply) {
                const reply = document.createElement("button");
                reply.type = "button";
                reply.className = "link-btn";
                reply.setAttribute("data-reply-to", comment.commentId);
                reply.textContent = "Reply";
                actions.appendChild(reply);
            }

            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "link-btn link-btn--danger";
            remove.setAttribute("data-delete-comment", comment.commentId);
            remove.textContent = "Remove";
            actions.appendChild(remove);

            body.appendChild(actions);

            if (!isReply) {
                const replies = document.createElement("ul");
                replies.className = "comment-replies";
                replies.setAttribute("data-replies-for", comment.commentId);
                body.appendChild(replies);
            }

            li.appendChild(avatar);
            li.appendChild(body);

            requestAnimationFrame(function () { li.classList.remove("is-entering"); });
            return li;
        }

        function setReplyTarget(id, authorName) {
            replyTo = id;

            if (!input) {
                return;
            }

            if (id) {
                input.placeholder = "Replying to " + authorName + "…";
                input.focus();
                panel.classList.add("is-replying");
            } else {
                input.placeholder = "Add useful context about this report.";
                panel.classList.remove("is-replying");
            }
        }

        if (input && counter) {
            const updateCounter = function () {
                counter.textContent = (MAX_COMMENT - input.value.length) + " characters left";
            };
            input.addEventListener("input", updateCounter);
            updateCounter();
        }

        if (form) {
            form.addEventListener("submit", async function (event) {
                event.preventDefault();

                const text = (input.value || "").trim();
                if (!text) {
                    toast("warning", "Write something before posting.");
                    return;
                }

                submit.disabled = true;
                submit.classList.add("is-loading");

                try {
                    const comment = await send("/api/v1/reports/" + reportId + "/comments", "POST", {
                        text: text,
                        parentCommentId: replyTo
                    });

                    const node = buildComment(comment, Boolean(replyTo));

                    if (replyTo) {
                        const host = list.querySelector('[data-replies-for="' + replyTo + '"]');
                        if (host) {
                            host.appendChild(node);
                        } else {
                            list.prepend(node);
                        }
                    } else {
                        list.prepend(node);
                    }

                    input.value = "";
                    input.dispatchEvent(new Event("input"));
                    setReplyTarget(null);
                    bumpCount(1);
                } catch (error) {
                    toast("error", error.message);
                } finally {
                    submit.disabled = false;
                    submit.classList.remove("is-loading");
                }
            });
        }

        panel.addEventListener("click", async function (event) {
            const replyButton = event.target.closest("[data-reply-to]");
            if (replyButton) {
                const host = replyButton.closest(".comment");
                const author = host ? host.querySelector(".comment-head strong") : null;
                setReplyTarget(replyButton.getAttribute("data-reply-to"), author ? author.textContent : "this comment");
                return;
            }

            const deleteButton = event.target.closest("[data-delete-comment]");
            if (!deleteButton) {
                return;
            }

            const commentId = deleteButton.getAttribute("data-delete-comment");
            deleteButton.disabled = true;

            try {
                await send("/api/v1/reports/" + reportId + "/comments/" + commentId, "DELETE");

                const node = panel.querySelector('[data-comment-id="' + commentId + '"]');
                if (node) {
                    node.classList.add("is-removed");
                    const text = node.querySelector(".comment-text");
                    const nameEl = node.querySelector(".comment-head strong");
                    const actions = node.querySelector(".comment-actions");
                    if (text) { text.textContent = "This comment was removed."; }
                    if (nameEl) { nameEl.textContent = "Removed comment"; }
                    if (actions) { actions.remove(); }
                }

                bumpCount(-1);
            } catch (error) {
                toast("error", error.message);
                deleteButton.disabled = false;
            }
        });

        if (more) {
            more.addEventListener("click", async function () {
                const page = parseInt(more.getAttribute("data-next-page"), 10) || 2;
                more.disabled = true;
                more.classList.add("is-loading");

                try {
                    const data = await send("/api/v1/reports/" + reportId + "/comments?page=" + page, "GET");

                    data.items.forEach(function (comment) {
                        const node = buildComment(comment, false);
                        list.appendChild(node);

                        const host = node.querySelector('[data-replies-for="' + comment.commentId + '"]');
                        (comment.replies || []).forEach(function (reply) {
                            host.appendChild(buildComment(reply, true));
                        });
                    });

                    if (data.hasNext) {
                        more.setAttribute("data-next-page", String(page + 1));
                    } else {
                        more.remove();
                    }
                } catch (error) {
                    toast("error", error.message);
                } finally {
                    more.disabled = false;
                    more.classList.remove("is-loading");
                }
            });
        }
    }

    document.addEventListener("DOMContentLoaded", function () {
        initVotes();
        initComments();
    });
})();

