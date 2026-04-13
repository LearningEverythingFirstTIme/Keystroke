// KEYSTROKE Landing Page — Minimal JS
// Mobile menu, smooth scroll, scroll animations, active nav, word reveal, text scramble

(function () {
  'use strict';

  // Mobile menu toggle
  const hamburger = document.querySelector('.nav__hamburger');
  const mobileLinks = document.querySelectorAll('.nav__mobile a');

  if (hamburger) {
    hamburger.addEventListener('click', () => {
      document.body.classList.toggle('menu-open');
    });
  }

  mobileLinks.forEach(link => {
    link.addEventListener('click', () => {
      document.body.classList.remove('menu-open');
    });
  });

  // Smooth scroll for all anchor links
  document.querySelectorAll('a[href^="#"]').forEach(anchor => {
    anchor.addEventListener('click', (e) => {
      const target = document.querySelector(anchor.getAttribute('href'));
      if (target) {
        e.preventDefault();
        target.scrollIntoView({ behavior: 'smooth' });
      }
    });
  });

  // Scroll-triggered fade-in animations
  const animatedEls = document.querySelectorAll('.animate-on-scroll');

  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver((entries) => {
      entries.forEach(entry => {
        if (entry.isIntersecting) {
          entry.target.classList.add('visible');
          observer.unobserve(entry.target);
        }
      });
    }, { threshold: 0.1, rootMargin: '0px 0px -40px 0px' });

    animatedEls.forEach(el => observer.observe(el));
  } else {
    // Fallback: show everything
    animatedEls.forEach(el => el.classList.add('visible'));
  }

  // Active nav link highlighting on scroll
  const sections = document.querySelectorAll('section[id]');
  const navLinks = document.querySelectorAll('.nav__link');

  function updateActiveNav() {
    const scrollY = window.scrollY + 120;

    sections.forEach(section => {
      const top = section.offsetTop;
      const height = section.offsetHeight;
      const id = section.getAttribute('id');

      if (scrollY >= top && scrollY < top + height) {
        navLinks.forEach(link => {
          link.classList.remove('active');
          if (link.getAttribute('href') === '#' + id) {
            link.classList.add('active');
          }
        });
      }
    });
  }

  window.addEventListener('scroll', updateActiveNav, { passive: true });
  updateActiveNav();

  // ========================================================
  // Word-by-word LLM-style reveal on section titles
  // ========================================================
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  document.querySelectorAll('.section-title').forEach(title => {
    // Grab the raw text content before we wrap anything
    // We need to handle <br> tags: preserve them as linebreaks
    const html = title.innerHTML;
    // Split on <br> variants, process each line, rejoin with <br>
    const lines = html.split(/<br\s*\/?>/i);
    let rebuilt = '';

    lines.forEach((line, lineIdx) => {
      // Strip any existing HTML tags (like data-text attr cruft) and get words
      const textOnly = line.replace(/<[^>]+>/g, '').trim();
      const words = textOnly.split(/\s+/).filter(w => w.length > 0);

      words.forEach((word, i) => {
        const cls = reducedMotion ? 'word revealed' : 'word';
        rebuilt += '<span class="' + cls + '">' + word + '</span>';
        if (i < words.length - 1) rebuilt += ' ';
      });

      if (lineIdx < lines.length - 1) rebuilt += '<br>';
    });

    // Add the trailing cursor element
    rebuilt += '<span class="title-cursor"></span>';
    title.innerHTML = rebuilt;
  });

  // Reveal words sequentially when the title scrolls into view
  if ('IntersectionObserver' in window && !reducedMotion) {
    const titleObserver = new IntersectionObserver((entries) => {
      entries.forEach(entry => {
        if (entry.isIntersecting) {
          revealWords(entry.target);
          titleObserver.unobserve(entry.target);
        }
      });
    }, { threshold: 0.3 });

    document.querySelectorAll('.section-title').forEach(title => {
      titleObserver.observe(title);
    });
  }

  function revealWords(titleEl) {
    const words = titleEl.querySelectorAll('.word');
    const cursor = titleEl.querySelector('.title-cursor');
    if (!words.length) return;

    // Show cursor
    if (cursor) cursor.classList.add('active');

    // Reveal each word at ~human reading speed (70ms per word)
    const delay = 70;
    words.forEach((word, i) => {
      setTimeout(() => {
        word.classList.add('revealed');
      }, i * delay);
    });

    // Hide cursor after all words revealed + a short linger
    const totalTime = words.length * delay + 400;
    if (cursor) {
      setTimeout(() => {
        cursor.classList.remove('active');
        cursor.classList.add('done');
      }, totalTime);
    }
  }

  // ========================================================
  // Sequential text stream on hero subtitle (LLM-style)
  // ========================================================
  function streamText(el) {
    if (reducedMotion) return;

    const final = el.textContent.trim();
    const words = final.split(/\s+/);
    el.textContent = '';
    el.style.visibility = 'visible';

    let i = 0;
    const delay = 50; // ms per word — fast, like streaming output

    function next() {
      if (i < words.length) {
        el.textContent += (i > 0 ? ' ' : '') + words[i];
        i++;
        setTimeout(next, delay);
      }
    }

    next();
  }

  const heroSub = document.querySelector('.hero__subtitle');
  if (heroSub) {
    const finalText = heroSub.textContent;
    // Hide text until reveal triggers
    if (!reducedMotion) heroSub.textContent = '';

    if ('IntersectionObserver' in window) {
      const heroObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
          if (entry.isIntersecting) {
            heroSub.textContent = '';
            // Restore full text so streamText can read it
            const temp = finalText;
            heroSub.textContent = temp;
            streamText(entry.target);
            heroObserver.unobserve(entry.target);
          }
        });
      }, { threshold: 0.5 });
      heroObserver.observe(heroSub);
    }
  }
})();
