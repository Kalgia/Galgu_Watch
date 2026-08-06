import { renderMarkdown } from './md.js';

const $ = sel => document.querySelector(sel);
const el = (tag, cls) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
};

const WEEKDAYS = ['일', '월', '화', '수', '목', '금', '토'];

let cfg = null;
let cur = { y: 0, m: 0 };      // 표시 중인 달
let monthDays = new Map();     // date -> totalSec
let selDate = null;
let dayData = null;
let noteDirty = false;
let saveTimer = 0;

/* ---------- C# 브리지 ---------- */
const pending = new Map();
let seq = 0;
window.chrome.webview.addEventListener('message', e => {
  const m = e.data;
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  if (m.error) p.reject(new Error(m.error));
  else p.resolve(m.result);
});
function rpc(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, params });
  });
}

/* ---------- 유틸 ---------- */
const pad = n => String(n).padStart(2, '0');
const hm = s => `${Math.floor(s / 3600)}:${pad(Math.floor(s % 3600 / 60))}`;
const hms = s => `${Math.floor(s / 3600)}:${pad(Math.floor(s % 3600 / 60))}:${pad(Math.floor(s % 60))}`;
const parseTs = t => new Date(t.replace(' ', 'T'));
const fmtTime = t => {
  const d = parseTs(t);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
};
const dateLabel = d => {
  const [y, m, dd] = d.split('-').map(Number);
  return `${m}월 ${dd}일 (${WEEKDAYS[new Date(y, m - 1, dd).getDay()]})`;
};

/* ---------- 캘린더 ---------- */
let monthGoals = new Map();   // date -> [목표 제목들]

async function loadMonth() {
  const r = await rpc('getMonth', { year: cur.y, month: cur.m });
  monthDays = new Map(r.days.map(d => [d.date, d.totalSec]));
  monthGoals = new Map((r.goalsDue || []).map(g => [g.date, g.titles]));
  cfg.today = r.today;
  updateStreak(r.streak);
  renderCalendar();
}

function updateStreak(n) {
  const s = $('#streak');
  if (n > 0) {
    s.hidden = false;
    s.textContent = `🔥 연속 ${n}일`;
  } else {
    s.hidden = true;
  }
}

function renderCalendar() {
  $('#month-label').textContent = `${cur.y}년 ${cur.m}월`;
  const total = [...monthDays.values()].reduce((a, b) => a + b, 0);
  const n = monthDays.size;
  $('#month-stats').textContent = n
    ? `합계 ${hm(total)} · 하루평균 ${hm(Math.round(total / n))} · ${n}일 기록`
    : '이번 달 기록 없음';

  const grid = $('#cal-grid');
  grid.innerHTML = '';
  for (const w of WEEKDAYS) {
    const head = el('div', 'cal-head');
    head.textContent = w;
    grid.appendChild(head);
  }
  const firstDow = new Date(cur.y, cur.m - 1, 1).getDay();
  const daysInMonth = new Date(cur.y, cur.m, 0).getDate();
  const max = Math.max(...monthDays.values(), 1);

  for (let i = 0; i < firstDow; i++) grid.appendChild(el('div', 'cal-cell empty'));
  for (let d = 1; d <= daysInMonth; d++) {
    const date = `${cur.y}-${pad(cur.m)}-${pad(d)}`;
    const sec = monthDays.get(date) || 0;
    const cell = el('div', 'cal-cell');
    if (sec > 0) {
      const pct = Math.round(15 + 55 * (sec / max));
      cell.style.background = `color-mix(in srgb, var(--heat) ${pct}%, var(--cell))`;
    }
    const goalSec = (cfg.goalMinutes | 0) * 60;
    if (goalSec > 0 && sec >= goalSec) cell.classList.add('goal');
    if (date === cfg.today) cell.classList.add('today');
    if (date === selDate) cell.classList.add('selected');
    const day = el('span', 'cal-day');
    day.textContent = d;
    cell.appendChild(day);
    if (sec > 0) {
      const t = el('span', 'cal-time');
      t.textContent = hm(sec);
      cell.appendChild(t);
    }
    const gd = monthGoals.get(date);
    if (gd && gd.length) {
      const g = el('span', 'cal-goal');
      g.textContent = '🎯';
      g.title = '목표: ' + gd.join(', ');
      cell.appendChild(g);
    }
    cell.onclick = () => selectDay(date);
    grid.appendChild(cell);
  }
}

/* ---------- 일별 상세 ---------- */
async function selectDay(date) {
  await flushNote();
  showView('cal');
  selDate = date;
  dayData = await rpc('getDay', { date });
  renderCalendar();
  renderDay();
}

function renderDay() {
  $('#day-panel').hidden = false;
  $('#day-title').textContent = dateLabel(selDate);
  renderDayGoals();
  renderDayTotal();
  renderTimeline();
  renderShots();
  loadNoteEditor();
}

function renderDayGoals() {
  const dg = $('#day-goals');
  if (dayData.dueGoals && dayData.dueGoals.length) {
    dg.hidden = false;
    dg.textContent = '🎯 이 날까지: ' + dayData.dueGoals.join(' · ');
  } else {
    dg.hidden = true;
  }
}

function renderDayTotal() {
  $('#day-total').textContent = dayData.sessions.length || dayData.totalSec
    ? `총 ${hms(dayData.totalSec)} · 세션 ${dayData.sessions.length}개` + (dayData.running ? ' · 측정 중' : '')
    : '기록 없음';
}

function renderTimeline() {
  const tl = $('#timeline');
  tl.innerHTML = '';
  const [y, m, d] = selDate.split('-').map(Number);
  const base = new Date(y, m - 1, d, cfg.dayStartHour, 0, 0).getTime();
  const span = 24 * 3600 * 1000;
  for (const s of dayData.sessions) {
    const a = Math.max(0, Math.min(parseTs(s.startedAt).getTime() - base, span));
    const b = Math.max(0, Math.min(parseTs(s.endedAt).getTime() - base, span));
    const seg = el('div', 'tl-seg');
    seg.style.left = `${(a / span * 100).toFixed(2)}%`;
    seg.style.width = `${Math.max(0.4, (b - a) / span * 100).toFixed(2)}%`;
    seg.title = `${fmtTime(s.startedAt)}–${fmtTime(s.endedAt)} (${hms(s.durationSec)})`;
    tl.appendChild(seg);
  }
  const labels = $('#tl-labels');
  labels.innerHTML = '';
  for (let i = 0; i <= 24; i += 6) {
    const l = el('span');
    l.textContent = `${(cfg.dayStartHour + i) % 24}시`;
    labels.appendChild(l);
  }
}

function renderShots() {
  const g = $('#gallery');
  g.innerHTML = '';
  $('#shots-count').textContent = dayData.shots.length ? `${dayData.shots.length}장` : '';
  const empty = $('#gallery-empty');
  empty.hidden = dayData.shots.length > 0;
  empty.textContent = `아직 스크린샷이 없어요. 측정 중 ${cfg.captureIntervalMin}분마다 자동 캡처되고, 오버레이의 📷 버튼으로 직접 찍을 수도 있어요.`;

  for (const s of dayData.shots) {
    const card = el('div', 'shot');
    const img = el('img');
    img.loading = 'lazy';
    img.src = s.url;
    img.alt = s.takenAt;
    img.onclick = () => {
      $('#lightbox-img').src = s.url;
      $('#lightbox').hidden = false;
    };
    const meta = el('div', 'shot-meta');
    const time = el('span');
    time.textContent = `${fmtTime(s.takenAt)}${s.kind === 'manual' ? ' 📷' : ''}`;
    const btns = el('div', 'shot-btns');
    const btnIns = el('button', 'shot-btn');
    btnIns.textContent = '일지에 넣기';
    btnIns.onclick = () => insertImage(s);
    const btnDel = el('button', 'shot-btn danger');
    btnDel.textContent = '삭제';
    btnDel.onclick = async () => {
      if (!confirm('이 스크린샷을 삭제할까요?')) return;
      await rpc('deleteShot', { id: s.id });
      dayData = await rpc('getDay', { date: selDate });
      renderShots();
    };
    btns.append(btnIns, btnDel);
    meta.append(time, btns);
    card.append(img, meta);
    g.appendChild(card);
  }
}

/* ---------- 작업일지 ---------- */
function noteTemplate() {
  const lines = [`# ${dateLabel(selDate)} 작업일지`, ''];
  if (dayData.sessions.length)
    lines.push(`- 총 작업시간: ${hms(dayData.totalSec)} (세션 ${dayData.sessions.length}개)`, '');
  lines.push('## 오늘 한 일', '', '', '## 메모', '');
  return lines.join('\n');
}

function loadNoteEditor() {
  const ta = $('#note-edit');
  ta.value = dayData.note ?? noteTemplate();
  noteDirty = false;
  $('#note-status').textContent = dayData.note != null ? '저장된 일지' : '새 일지 — 입력하면 자동 저장';
  updatePreview();
}

function updatePreview() {
  $('#note-preview').innerHTML = renderMarkdown($('#note-edit').value);
}

async function flushNote() {
  clearTimeout(saveTimer);
  if (!noteDirty || !selDate) return;
  noteDirty = false;
  await rpc('saveNote', { date: selDate, content: $('#note-edit').value });
  const now = new Date();
  $('#note-status').textContent = `저장됨 ${pad(now.getHours())}:${pad(now.getMinutes())}`;
}

function insertImage(s) {
  const ta = $('#note-edit');
  const mdImg = `\n![${fmtTime(s.takenAt)} 캡처](${s.url})\n`;
  const pos = ta.selectionStart ?? ta.value.length;
  ta.value = ta.value.slice(0, pos) + mdImg + ta.value.slice(pos);
  noteDirty = true;
  $('#note-status').textContent = '입력 중…';
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushNote, 1500);
  updatePreview();
}

/* ---------- 창 포커스 시 최신화 (노트 입력 내용은 유지) ---------- */
async function refreshOnFocus() {
  if (!cfg) return;
  try {
    await loadMonth();
    if (curView === 'goals') await loadGoals();
    if (selDate) {
      dayData = await rpc('getDay', { date: selDate });
      renderDayGoals();
      renderDayTotal();
      renderTimeline();
      renderShots();
    }
  } catch { /* 창 닫히는 중이면 무시 */ }
}

/* ---------- 이벤트 ---------- */
$('#btn-prev').onclick = async () => {
  await flushNote();
  showView('cal');
  cur.m--;
  if (cur.m === 0) { cur.m = 12; cur.y--; }
  await loadMonth();
};
$('#btn-next').onclick = async () => {
  await flushNote();
  showView('cal');
  cur.m++;
  if (cur.m === 13) { cur.m = 1; cur.y++; }
  await loadMonth();
};
$('#btn-today').onclick = async () => {
  const [y, m] = cfg.today.split('-').map(Number);
  cur = { y, m };
  await loadMonth();
  await selectDay(cfg.today);
};
$('#open-data').onclick = e => {
  e.preventDefault();
  rpc('openDataFolder');
};
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem('galgu-theme', theme);
  const btn = $('#btn-theme');
  btn.textContent = theme === 'dark' ? '☀️' : '🌙';
  btn.title = theme === 'dark' ? '화이트 모드로 전환' : '다크 모드로 전환';
}
$('#btn-theme').onclick = () => {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  applyTheme(next);
  rpc('setTheme', { theme: next });
};

/* ---------- 설정 ---------- */
function fillSettings(s) {
  $('#set-interval').value = s.captureIntervalMin;
  $('#set-retention').value = s.retentionDays;
  $('#set-idle').value = s.idleThresholdMin;
  const sel = $('#set-daystart');
  sel.innerHTML = '';
  for (let h = 0; h <= 12; h++) {
    const o = document.createElement('option');
    o.value = h;
    o.textContent = h === 0 ? '0시 (자정)' : `${h}시`;
    sel.appendChild(o);
  }
  sel.value = s.dayStartHour;
  $('#set-monitor').value = s.captureMonitor;
  const op = Math.round(s.overlayOpacity * 100);
  $('#set-opacity').value = op;
  $('#set-opacity-v').textContent = op + '%';
  $('#set-goal').value = s.goalMinutes;
  $('#set-presence').checked = !!s.discordPresence;
  $('#set-cheer').checked = !!s.discordCheer;
  $('#set-name').value = s.displayName || '';
  $('#set-webhook').value = s.discordWebhookUrl || '';
  const sd = $('#set-shotdir');
  sd.value = s.screenshotsDir;
  sd.dataset.default = s.screenshotsDirDefault;
}
$('#set-shotdir-pick').onclick = async () => {
  const p = await rpc('pickFolder');
  if (p) $('#set-shotdir').value = p;
};
$('#set-shotdir-reset').onclick = () => {
  const sd = $('#set-shotdir');
  sd.value = sd.dataset.default || '';
};
$('#btn-settings').onclick = async () => {
  fillSettings(await rpc('getSettings'));
  $('#settings').hidden = false;
};
$('#set-close').onclick = () => { $('#settings').hidden = true; };
$('#settings').onclick = e => { if (e.target === $('#settings')) $('#settings').hidden = true; };
$('#set-opacity').oninput = e => { $('#set-opacity-v').textContent = e.target.value + '%'; };
$('#set-save').onclick = async () => {
  await rpc('saveSettings', {
    captureIntervalMin: +$('#set-interval').value || 10,
    retentionDays: Math.max(0, +$('#set-retention').value || 0),
    idleThresholdMin: +$('#set-idle').value || 5,
    dayStartHour: +$('#set-daystart').value || 0,
    overlayOpacity: (+$('#set-opacity').value || 100) / 100,
    goalMinutes: Math.max(0, +$('#set-goal').value || 0),
    captureMonitor: $('#set-monitor').value,
    discordPresence: $('#set-presence').checked,
    discordCheer: $('#set-cheer').checked,
    displayName: $('#set-name').value.trim(),
    discordWebhookUrl: $('#set-webhook').value.trim(),
    screenshotsDir: $('#set-shotdir').value.trim(),
  });
  cfg = await rpc('getConfig');
  await loadMonth();
  if (selDate) {
    dayData = await rpc('getDay', { date: selDate });
    renderDayTotal();
    renderTimeline();
    renderShots();
  }
  $('#settings').hidden = true;
};

/* ---------- 공유 내보내기 ---------- */
function openShare() {
  if (!dayData || !selDate) return;
  const grid = $('#share-shots');
  grid.innerHTML = '';
  $('#share-empty').hidden = dayData.shots.length > 0;
  for (const s of dayData.shots) {
    const item = el('label', 'share-shot');
    const cb = el('input');
    cb.type = 'checkbox';
    cb.checked = true;
    cb.dataset.id = s.id;
    const img = el('img');
    img.loading = 'lazy';
    img.src = s.url;
    const t = el('span', 'small muted');
    t.textContent = fmtTime(s.takenAt);
    item.append(cb, img, t);
    grid.appendChild(item);
  }
  $('#share').hidden = false;
}

function selectedShots() {
  const ids = new Set([...$('#share-shots').querySelectorAll('input:checked')].map(i => +i.dataset.id));
  return dayData.shots.filter(s => ids.has(s.id));
}

/* 공유 카드 DOM — 테마와 무관하게 항상 밝게, 화면 밖(페이지 하단 아래)에 만들어 캡처만 한다 */
function buildShareCard(shots) {
  const wrap = el('div');
  wrap.style.cssText = 'position:absolute;left:0;width:860px;background:#ffffff;color:#1f2937;' +
    "font-family:'Segoe UI','Malgun Gothic',sans-serif;padding:34px 38px;";
  wrap.style.top = (document.documentElement.scrollHeight + 300) + 'px';

  const goalSec = (cfg.goalMinutes | 0) * 60;
  const goalTxt = goalSec > 0 && dayData.totalSec >= goalSec ? ' · 목표 달성 ✓' : '';
  let html = `
    <div style="display:flex;justify-content:space-between;align-items:baseline;border-bottom:2px solid #16a34a;padding-bottom:12px;margin-bottom:16px">
      <div style="font-size:24px;font-weight:700">${dateLabel(selDate)} 작업일지</div>
      <div style="font-size:14px;color:#94a3b8">Galgu Watch</div>
    </div>
    <div style="font-size:16px;margin-bottom:14px">⏱ 총 <b>${hms(dayData.totalSec)}</b> · 세션 ${dayData.sessions.length}개${goalTxt}</div>`;

  const [y, m, d] = selDate.split('-').map(Number);
  const base = new Date(y, m - 1, d, cfg.dayStartHour, 0, 0).getTime();
  const span = 24 * 3600 * 1000;
  let segs = '';
  for (const s of dayData.sessions) {
    const a = Math.max(0, Math.min(parseTs(s.startedAt).getTime() - base, span));
    const b = Math.max(0, Math.min(parseTs(s.endedAt).getTime() - base, span));
    segs += `<div style="position:absolute;top:4px;bottom:4px;border-radius:4px;background:#22c55e;left:${(a / span * 100).toFixed(2)}%;width:${Math.max(0.4, (b - a) / span * 100).toFixed(2)}%"></div>`;
  }
  html += `<div style="position:relative;height:24px;background:#eef2f8;border-radius:7px;overflow:hidden;margin-bottom:18px">${segs}</div>`;

  if (shots.length) {
    html += '<div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:18px">';
    for (const s of shots) html += `<img src="${s.url}" style="width:100%;border-radius:8px;border:1px solid #dbe3ee">`;
    html += '</div>';
  }
  html += `<div style="font-size:14px;line-height:1.65">${renderMarkdown($('#note-edit').value)}</div>`;
  wrap.innerHTML = html;
  document.body.appendChild(wrap);
  return wrap;
}

async function exportCard(upload) {
  const btn = upload ? $('#share-discord-btn') : $('#share-card-btn');
  btn.disabled = true;
  await flushNote();
  const card = buildShareCard(selectedShots());
  try {
    await Promise.all([...card.querySelectorAll('img')].map(i => i.decode().catch(() => {})));
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    const rect = card.getBoundingClientRect();
    await rpc('captureCard', {
      date: selDate,
      x: rect.left + window.scrollX,
      y: rect.top + window.scrollY,
      w: rect.width,
      h: rect.height,
      upload: !!upload,
    });
    $('#share').hidden = true;
    if (upload) alert('디스코드 채널에 올렸어요 ✅');
  } catch (err) {
    alert((upload ? '업로드 실패: ' : '카드 저장 실패: ') + err.message);
  } finally {
    card.remove();
    btn.disabled = false;
  }
}

async function exportHtml() {
  const btn = $('#share-html-btn');
  btn.disabled = true;
  await flushNote();
  try {
    await rpc('exportDay', {
      date: selDate,
      noteHtml: renderMarkdown($('#note-edit').value),
      shotIds: selectedShots().map(s => s.id),
    });
    $('#share').hidden = true;
  } catch (err) {
    alert('내보내기 실패: ' + err.message);
  } finally {
    btn.disabled = false;
  }
}

/* ---------- 목표 탭 ---------- */
let goals = [];
let curView = 'cal';

function showView(v) {
  curView = v;
  $('#cal-grid').hidden = v !== 'cal';
  $('#day-panel').hidden = v !== 'cal' || !selDate;
  $('#goals-panel').hidden = v !== 'goals';
  $('#btn-goals').classList.toggle('active', v === 'goals');
}

async function loadGoals() {
  goals = await rpc('getGoals');
  renderGoals();
}

function dday(dueDate, done) {
  if (!dueDate) return null;
  const d = Math.round((parseTs(dueDate + ' 00:00:00') - parseTs(cfg.today + ' 00:00:00')) / 86400000);
  if (done) return { text: dueDate.slice(5).replace('-', '/'), cls: '' };
  if (d > 0) return { text: `D-${d}`, cls: d <= 3 ? 'soon' : '' };
  if (d === 0) return { text: 'D-DAY', cls: 'soon' };
  return { text: `${-d}일 지남`, cls: 'over' };
}

async function goalChanged() {
  await loadGoals();
  await loadMonth();          // 캘린더 🎯 갱신
  if (selDate) {
    dayData = await rpc('getDay', { date: selDate });
    renderDayGoals();
  }
}

function goalRow(g) {
  const row = el('div', 'goal-row' + (g.done ? ' done' : ''));
  const cb = el('input');
  cb.type = 'checkbox';
  cb.checked = g.done;
  cb.title = g.done ? '달성 취소 (체크아웃)' : '달성! (체크)';
  cb.onchange = async () => {
    await rpc('toggleGoal', { id: g.id });
    await goalChanged();
  };
  const title = el('span', 'goal-title');
  title.textContent = g.title;
  title.title = '클릭해서 내용 수정';
  title.onclick = () => editGoalTitle(row, g, title);
  const due = el('input');
  due.type = 'date';
  due.value = g.dueDate || '';
  due.title = '목표 날짜 (비우면 없음)';
  due.onchange = async () => {
    await rpc('updateGoal', { id: g.id, title: g.title, dueDate: due.value });
    await goalChanged();
  };
  const chip = el('span', 'goal-chip');
  const dd = dday(g.dueDate, g.done);
  if (dd) {
    chip.textContent = dd.text;
    if (dd.cls) chip.classList.add(dd.cls);
  } else {
    chip.hidden = true;
  }
  const del = el('button', 'shot-btn danger');
  del.textContent = '삭제';
  del.onclick = async () => {
    if (!confirm(`목표를 삭제할까요?\n"${g.title}"`)) return;
    await rpc('deleteGoal', { id: g.id });
    await goalChanged();
  };
  row.append(cb, title, due, chip);
  if (g.done && g.doneAt) {
    const da = el('span', 'muted small');
    da.textContent = `완료 ${g.doneAt.slice(5, 10).replace('-', '/')}`;
    row.appendChild(da);
  }
  row.appendChild(del);
  return row;
}

function editGoalTitle(row, g, titleEl) {
  const inp = el('input', 'goal-edit');
  inp.type = 'text';
  inp.value = g.title;
  inp.maxLength = 200;
  row.replaceChild(inp, titleEl);
  inp.focus();
  inp.select();
  let committed = false;
  const commit = async () => {
    if (committed) return;
    committed = true;
    const t = inp.value.trim();
    if (t && t !== g.title) {
      await rpc('updateGoal', { id: g.id, title: t, dueDate: g.dueDate || '' });
      await goalChanged();
    } else {
      renderGoals();
    }
  };
  inp.onblur = commit;
  inp.onkeydown = e => {
    if (e.key === 'Enter') inp.blur();
    if (e.key === 'Escape') { committed = true; renderGoals(); }
  };
}

function renderGoals() {
  const act = $('#goal-list');
  const doneL = $('#goal-done-list');
  act.innerHTML = '';
  doneL.innerHTML = '';
  const active = goals.filter(g => !g.done);
  const done = goals.filter(g => g.done);
  if (!active.length) {
    const p = el('p', 'muted small');
    p.textContent = '진행 중인 목표가 없어요. 위에서 새 목표를 추가해보세요.';
    act.appendChild(p);
  }
  for (const g of active) act.appendChild(goalRow(g));
  $('#goal-done-head').hidden = !done.length;
  for (const g of done) doneL.appendChild(goalRow(g));
}

async function addGoal() {
  const t = $('#goal-title').value.trim();
  if (!t) return;
  await rpc('addGoal', { title: t, dueDate: $('#goal-due').value });
  $('#goal-title').value = '';
  $('#goal-due').value = '';
  await goalChanged();
  $('#goal-title').focus();
}

$('#btn-goals').onclick = async () => {
  if (curView === 'goals') {
    showView('cal');
    return;
  }
  await loadGoals();
  showView('goals');
};
$('#goal-add-btn').onclick = addGoal;
$('#goal-title').addEventListener('keydown', e => { if (e.key === 'Enter') addGoal(); });

$('#btn-share').onclick = openShare;
$('#share-close').onclick = () => { $('#share').hidden = true; };
$('#share').onclick = e => { if (e.target === $('#share')) $('#share').hidden = true; };
$('#share-card-btn').onclick = () => exportCard(false);
$('#share-discord-btn').onclick = () => exportCard(true);
$('#share-html-btn').onclick = exportHtml;

$('#lightbox').onclick = () => { $('#lightbox').hidden = true; };
$('#note-edit').addEventListener('input', () => {
  noteDirty = true;
  $('#note-status').textContent = '입력 중…';
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushNote, 1500);
  updatePreview();
});
window.addEventListener('blur', flushNote);
window.addEventListener('focus', refreshOnFocus);

/* ---------- 시작 ---------- */
async function init() {
  cfg = await rpc('getConfig');
  applyTheme(cfg.theme === 'dark' ? 'dark' : 'light');
  const [y, m] = cfg.today.split('-').map(Number);
  cur = { y, m };
  await loadMonth();
  await selectDay(cfg.today);
  rpc('ready', { today: cfg.today, recordedDays: monthDays.size });
}
init().catch(err => {
  document.body.innerHTML =
    `<pre style="color:#f87171;padding:24px;white-space:pre-wrap">초기화 실패\n${err.stack || err}</pre>`;
});
