// 작업일지용 초경량 마크다운 렌더러 — 외부 라이브러리 없이 필요한 문법만 지원
// 지원: 제목(#), 굵게/기울임, 인라인 코드, 코드 블록(```), 목록(-, 1.), 인용(>), 구분선, 이미지, 링크

const esc = s => s
  .replace(/&/g, '&amp;')
  .replace(/</g, '&lt;')
  .replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;');

// 인라인 문법 — 입력은 이미 esc 처리된 문자열
function inline(s) {
  return s
    .replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, '<img src="$2" alt="$1">')
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>');
}

export function renderMarkdown(src) {
  const lines = src.split(/\r?\n/);
  const out = [];
  let para = [];
  let list = null;
  let code = null;

  const flushPara = () => {
    if (para.length) { out.push(`<p>${para.map(inline).join('<br>')}</p>`); para = []; }
  };
  const flushList = () => {
    if (list) {
      out.push(`<${list.tag}>` + list.items.map(i => `<li>${inline(i)}</li>`).join('') + `</${list.tag}>`);
      list = null;
    }
  };

  for (const raw of lines) {
    if (code !== null) {
      if (/^```/.test(raw)) { out.push(`<pre><code>${code.join('\n')}</code></pre>`); code = null; }
      else code.push(esc(raw));
      continue;
    }
    if (/^```/.test(raw)) { flushPara(); flushList(); code = []; continue; }

    const h = raw.match(/^(#{1,6})\s+(.*)$/);
    if (h) {
      flushPara(); flushList();
      out.push(`<h${h[1].length}>${inline(esc(h[2]))}</h${h[1].length}>`);
      continue;
    }
    if (/^\s*(---+|\*\*\*+)\s*$/.test(raw)) { flushPara(); flushList(); out.push('<hr>'); continue; }

    const bq = raw.match(/^>\s?(.*)$/);
    if (bq) { flushPara(); flushList(); out.push(`<blockquote>${inline(esc(bq[1]))}</blockquote>`); continue; }

    const ul = raw.match(/^\s*[-*]\s+(.*)$/);
    if (ul) {
      flushPara();
      if (!list || list.tag !== 'ul') { flushList(); list = { tag: 'ul', items: [] }; }
      list.items.push(esc(ul[1]));
      continue;
    }
    const ol = raw.match(/^\s*\d+\.\s+(.*)$/);
    if (ol) {
      flushPara();
      if (!list || list.tag !== 'ol') { flushList(); list = { tag: 'ol', items: [] }; }
      list.items.push(esc(ol[1]));
      continue;
    }

    if (/^\s*$/.test(raw)) { flushPara(); flushList(); continue; }
    flushList();
    para.push(esc(raw));
  }

  if (code !== null) out.push(`<pre><code>${code.join('\n')}</code></pre>`);
  flushPara();
  flushList();
  return out.join('\n');
}
