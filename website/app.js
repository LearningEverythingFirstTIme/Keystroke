/* =========================================================================
   Keystroke — "Ghostwriter"
   - Ghost-reveal for display headlines (hero on load, others on scroll-in)
   - Sticky nav shadow
   - Infinite keycap marquee (clones track for seamless loop)
   - Live autocomplete demo (local phrase dictionary)
   ========================================================================= */

(() => {
  const prefersReduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ---------- Sticky nav ---------- */
  const nav = document.querySelector('.nav');
  const onScroll = () => {
    if (!nav) return;
    nav.classList.toggle('is-scrolled', window.scrollY > 8);
  };
  onScroll();
  window.addEventListener('scroll', onScroll, { passive: true });

  /* ---------- Mobile burger menu ---------- */
  const burger = document.getElementById('nav-burger');
  const navLinks = document.getElementById('nav-links');
  if (burger && navLinks) {
    const closeMenu = () => {
      burger.classList.remove('is-open');
      navLinks.classList.remove('is-open');
      if (nav) nav.classList.remove('menu-open');
      burger.setAttribute('aria-expanded', 'false');
      document.body.style.overflow = '';
    };
    burger.addEventListener('click', () => {
      const open = burger.classList.toggle('is-open');
      navLinks.classList.toggle('is-open', open);
      if (nav) nav.classList.toggle('menu-open', open);
      burger.setAttribute('aria-expanded', open);
      document.body.style.overflow = open ? 'hidden' : '';
    });
    navLinks.querySelectorAll('a').forEach((a) => {
      a.addEventListener('click', closeMenu);
    });
  }

  /* ---------- Ghost reveal ---------- */
  // Each [data-reveal] display: split each direct child span into word spans,
  // mark <em> tags, reveal one word at a time on trigger.
  const displays = document.querySelectorAll('[data-reveal]');
  displays.forEach(prepReveal);

  function prepReveal(el) {
    const lines = el.querySelectorAll(':scope > span, :scope > .line');
    lines.forEach((line) => {
      const html = line.innerHTML;
      // Walk nodes, split text into words
      const frag = document.createDocumentFragment();
      const wrapText = (text, isEm) => {
        const parts = text.split(/(\s+)/);
        parts.forEach((p) => {
          if (!p) return;
          if (/^\s+$/.test(p)) {
            frag.appendChild(document.createTextNode(p));
          } else {
            const span = document.createElement('span');
            span.dataset.word = '';
            if (isEm) span.classList.add('is-em');
            span.textContent = p;
            frag.appendChild(span);
          }
        });
      };
      const tmp = document.createElement('div');
      tmp.innerHTML = html;
      tmp.childNodes.forEach((node) => {
        if (node.nodeType === Node.TEXT_NODE) {
          wrapText(node.textContent, false);
        } else if (node.nodeType === Node.ELEMENT_NODE) {
          if (node.tagName === 'EM') {
            wrapText(node.textContent, true);
          } else {
            // Fallback: keep inline element, wrap its text
            wrapText(node.textContent, false);
          }
        }
      });
      line.innerHTML = '';
      line.appendChild(frag);
    });
  }

  function triggerReveal(el) {
    if (el.dataset.revealed === '1') return;
    el.dataset.revealed = '1';
    const words = el.querySelectorAll('[data-word]');
    if (prefersReduced) {
      words.forEach((w) => w.classList.add('is-resolved'));
      return;
    }
    words.forEach((w, i) => {
      setTimeout(() => w.classList.add('is-resolved'), 80 + i * 85);
    });
  }

  // Hero: trigger immediately on load
  const hero = document.querySelector('.hero [data-reveal]');
  if (hero) {
    requestAnimationFrame(() => {
      setTimeout(() => triggerReveal(hero), 120);
    });
  }

  // Others: trigger when scrolled into view
  const others = Array.from(displays).filter((d) => d !== hero);
  if ('IntersectionObserver' in window) {
    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            triggerReveal(entry.target);
            io.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.25, rootMargin: '0px 0px -10% 0px' }
    );
    others.forEach((el) => io.observe(el));
  } else {
    others.forEach(triggerReveal);
  }

  /* ---------- Keycap marquee (clone for seamless loop) ---------- */
  const track = document.getElementById('keys-track');
  if (track && !prefersReduced) {
    const clone = track.innerHTML;
    track.innerHTML = clone + clone;
  }

  /* ---------- Smooth anchor scroll with sticky-nav offset ---------- */
  document.querySelectorAll('a[href^="#"]').forEach((a) => {
    a.addEventListener('click', (e) => {
      const href = a.getAttribute('href');
      if (!href || href === '#') return;
      const target = document.querySelector(href);
      if (!target) return;
      e.preventDefault();
      const top = target.getBoundingClientRect().top + window.scrollY - 64;
      window.scrollTo({ top, behavior: prefersReduced ? 'auto' : 'smooth' });
    });
  });

  /* =====================================================================
     LIVE DEMO — pure-JS local autocomplete
     ===================================================================== */

  const input = document.getElementById('demo-input');
  const overlay = document.getElementById('demo-overlay');

  if (input && overlay) {
    // Dictionary of phrase prefixes → completions.
    // Keys are lowercased trigger prefixes. Values: list of candidates.
    const DICT = [
      // Email / polite
      ['thanks for',            ' reaching out — I appreciate you taking the time.'],
      ['thank you for',         ' your patience on this one, really appreciate it.'],
      ['looking forward',       ' to hearing your thoughts on this.'],
      ['let me know',           ' if that works, and I can put something on the calendar.'],
      ['please let me',         ' know if you need anything else from my end.'],
      ['i wanted to',           ' follow up on the thread from earlier this week.'],
      ['i just wanted',         ' to quickly check in on this before end of day.'],
      ['i hope this',           ' finds you well, and sorry for the delayed reply.'],
      ['sorry for the',         ' delayed response — things have been a little busy.'],
      ['as discussed',          ', I\'ve attached the notes from our call yesterday.'],
      ['as a follow',           '-up to our conversation, here are the next steps.'],
      ['per our conversation',  ', I put together a short summary below.'],
      ['happy to',              ' jump on a quick call if that\'s easier than email.'],
      ['feel free to',          ' reach out if you have any questions.'],
      ['just a quick',          ' note to share what I found this morning.'],
      // Slack-ish
      ['sounds good',           ' — I\'ll get started on it this afternoon.'],
      ['on it',                 ' — will send something over shortly.'],
      ['got it',                ', thanks! I\'ll loop back once it\'s ready for review.'],
      ['give me',               ' a few minutes and I\'ll ping you back.'],
      ['could you',             ' take another look when you get a chance?'],
      ['any chance',            ' you could send over the latest version?'],
      // Technical
      ['the issue',             ' appears to be in the request-handling middleware.'],
      ['the fix',               ' is a one-liner but it needed about forty lines of tests.'],
      ['i think the',           ' root cause is how we\'re caching the response.'],
      ['the root',              ' cause is a race condition between the two workers.'],
      ['we should',             ' probably add an integration test for this edge case.'],
      ['can you',               ' review the PR when you get a moment?'],
      // Writing
      ['the goal',              ' here is to keep things as simple as possible.'],
      ['what matters',          ' most is that the user never loses their work.'],
      ['this means',            ' we need to rethink how the sync layer behaves under load.'],
      ['in short',              ', the system prioritizes privacy at every boundary.'],
      ['keystroke is',          ' a system-wide AI autocomplete for Windows.'],
      ['your words',            ' stay yours — nothing leaves without your permission.'],
    ];

    // Sort by longest prefix first so longer matches win.
    DICT.sort((a, b) => b[0].length - a[0].length);

    let currentPrediction = '';

    const predictFor = (text) => {
      if (!text) return '';
      const lower = text.toLowerCase();
      // Try to match a suffix of `lower` against a dictionary prefix.
      for (const [trigger, completion] of DICT) {
        if (lower.endsWith(trigger)) {
          return completion;
        }
      }
      // Also try word-start matches for very short inputs.
      if (lower.length >= 3) {
        for (const [trigger, completion] of DICT) {
          if (trigger.startsWith(lower)) {
            // Return the remainder of the trigger + completion
            const remainder = trigger.slice(lower.length);
            return remainder + completion;
          }
        }
      }
      return '';
    };

    const escapeHTML = (s) =>
      s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    const render = () => {
      const text = input.value;
      const pred = predictFor(text);
      currentPrediction = pred;
      const typedHTML = `<span class="typed">${escapeHTML(text)}</span>`;
      const caretHTML = `<span class="caret-inline"></span>`;
      const ghostHTML = pred
        ? `<span class="ghost">${escapeHTML(pred)}</span>`
        : '';
      overlay.innerHTML = typedHTML + caretHTML + ghostHTML;
    };

    input.addEventListener('input', render);
    input.addEventListener('scroll', () => {
      overlay.scrollTop = input.scrollTop;
    });

    input.addEventListener('keydown', (e) => {
      if (e.key === 'Tab' && currentPrediction) {
        e.preventDefault();
        if (e.shiftKey) {
          // Accept one word
          const match = currentPrediction.match(/^(\s*\S+)/);
          if (match) {
            input.value += match[1];
            render();
          }
        } else {
          // Accept full
          input.value += currentPrediction;
          render();
        }
      } else if (e.key === 'Escape' && currentPrediction) {
        // Flash-dismiss: just clear the overlay briefly by faking no prediction
        currentPrediction = '';
        const text = input.value;
        overlay.innerHTML = `<span class="typed">${escapeHTML(text)}</span><span class="caret-inline"></span>`;
      }
    });

    // Seed with an inviting starter so visitors see ghost text right away.
    input.value = 'Thanks for';
    render();

    // Typing animation + glow when demo scrolls into view (desktop, once).
    if ('IntersectionObserver' in window && window.innerWidth > 780 && !prefersReduced) {
      const demoSection = document.getElementById('try');
      const editor = document.querySelector('.demo__editor');
      if (demoSection && editor) {
        const io2 = new IntersectionObserver(
          (entries) => {
            entries.forEach((entry) => {
              if (entry.isIntersecting && !input.dataset.animated) {
                input.dataset.animated = '1';
                io2.unobserve(demoSection);
                // Glow the editor border
                editor.classList.add('is-inviting');
                setTimeout(() => editor.classList.remove('is-inviting'), 3500);
                // Clear and retype "Thanks for" character by character
                input.value = '';
                render();
                const phrase = 'Thanks for';
                let idx = 0;
                const typeChar = () => {
                  if (idx < phrase.length) {
                    input.value += phrase[idx];
                    render();
                    idx++;
                    setTimeout(typeChar, 70 + Math.random() * 50);
                  }
                };
                setTimeout(typeChar, 400);
              }
            });
          },
          { threshold: 0.45 }
        );
        io2.observe(demoSection);
      }
    }
  }
})();
