import { API, D, PANEL_HEADERS, bridge, h, haptic, icon, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- agent widgets (Option B)
// agent-response: streams the AI agent's latest response, thoughts, tool executions and status.
// agent-prompt: interactive prompt input with voice dictation (Android speech bridge) and dispatch to Antigravity IDE.

function renderMarkdown(md) {
  if (!md) return '';
  let html = md
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');

  // Fenced code blocks ```code```
  html = html.replace(/```([a-zA-Z0-9_\-\.]*)\n?([\s\S]*?)```/g, (_, lang, code) => {
    return `<pre><code>${code.trim()}</code></pre>`;
  });

  // Inline code `code`
  html = html.replace(/`([^`\n]+)`/g, '<code>$1</code>');

  // Bold **text**
  html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

  // Italic *text*
  html = html.replace(/\*([^*]+)\*/g, '<em>$1</em>');

  // Bullet lists
  html = html.replace(/^\s*[-*]\s+(.+)$/gm, '<li>$1</li>');
  html = html.replace(/((?:<li>.*<\/li>\s*)+)/g, '<ul>$1</ul>');

  // Paragraphs
  html = html.split(/\n\s*\n/).map((p) => {
    p = p.trim();
    if (!p) return '';
    if (p.startsWith('<pre') || p.startsWith('<ul')) return p;
    return `<p>${p.replace(/\n/g, '<br>')}</p>`;
  }).join('');

  return html;
}

W['agent-response'] = {
  needs: ['work'],
  create(box, { o, w }) {
    box.classList.add('w-agent-response');

    const dot = h('div', 'ag-dot waiting');
    const projectEl = h('span', 'ag-project', 'Antigravity');
    const titleRow = h('div', 'ag-title-row', dot, projectEl);
    const modelEl = h('span', 'ag-model', 'Gemini');
    const head = h('div', 'ag-head', titleRow, modelEl);

    const statusLine = h('div', 'ag-status-line', t('w.agent.ready') || 'Ожидает ответа');
    const content = h('div', 'ag-content');
    content.innerHTML = `<div class="ag-empty">${t('w.agent.empty') || 'Ожидание ответа агента...'}</div>`;

    box.append(head, statusLine, content);

    let lastRenderedText = '';

    function updateData() {
      const chat = D.work?.agentChat;
      if (!chat) return;

      projectEl.textContent = chat.project || 'Antigravity';
      modelEl.textContent = chat.model || 'Gemini';

      const isBusy = chat.status === 'busy';
      dot.className = 'ag-dot ' + (isBusy ? 'busy' : 'waiting');

      if (isBusy) {
        statusLine.textContent = chat.currentTool ? `⚙️ ${chat.currentTool}` : (t('w.agent.thinking') || '🟡 Агент думает...');
      } else {
        statusLine.textContent = chat.currentTool ? `✅ ${chat.currentTool}` : (t('w.agent.ready') || '🟢 Ожидает ответа');
      }

      const text = chat.lastResponse || '';
      if (text !== lastRenderedText) {
        lastRenderedText = text;
        if (text) {
          content.innerHTML = renderMarkdown(text);
        } else {
          content.innerHTML = `<div class="ag-empty">${t('w.agent.empty') || 'Ожидание ответа агента...'}</div>`;
        }
      }
    }

    updateData();

    return {
      update() {
        updateData();
      }
    };
  }
};

W['agent-prompt'] = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('w-agent-prompt');

    const input = h('textarea', 'ag-input');
    input.placeholder = t('w.agent.promptPlaceholder') || 'Задайте задачу агенту...';
    input.rows = 1;

    const micBtn = h('button', 'ag-btn mic', icon('mic'));
    micBtn.setAttribute('aria-label', t('w.dictate.tap') || 'Голос');

    const sendBtn = h('button', 'ag-btn send', '🚀');
    sendBtn.setAttribute('aria-label', 'Отправить');

    const inputBox = h('div', 'ag-input-box', input, micBtn, sendBtn);
    box.append(inputBox);

    let alive = true;
    let session = 0;
    const canListen = !!(bridge && bridge.listen && bridge.canListen);
    if (!canListen) micBtn.style.display = 'none';

    const doneListening = () => {
      session = 0;
      micBtn.classList.remove('on');
    };

    micBtn.onclick = (e) => {
      e.stopPropagation();
      haptic();
      if (!canListen) return;
      if (session) {
        bridge.stopListening(session);
        doneListening();
        return;
      }
      if (!bridge.canListen()) {
        toast('Голосовой ввод недоступен', 'err');
        return;
      }
      session = bridge.listen('', false) || 0;
      if (!session) return;
      micBtn.classList.add('on');

      const prevHandler = window.onAndroidSpeech;
      window.onAndroidSpeech = (kind, text, id) => {
        if (!alive) return;
        if (kind === 'partial') {
          input.value = text;
        } else if (kind === 'final') {
          input.value = text;
          doneListening();
        } else if (kind === 'error') {
          doneListening();
        }
        if (prevHandler) prevHandler(kind, text, id);
      };
    };

    async function doSend() {
      const text = input.value.trim();
      if (!text) return;
      haptic();

      sendBtn.disabled = true;
      sendBtn.textContent = '⏳';

      try {
        const res = await fetch(`${API}/agent/prompt`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...PANEL_HEADERS },
          body: JSON.stringify({ prompt: text, target: o.target || 'Antigravity' })
        });

        if (!res.ok) {
          const errData = await res.json().catch(() => ({}));
          throw new Error(errData.error || `HTTP ${res.status}`);
        }

        sendBtn.textContent = '✓';
        sendBtn.classList.add('sent');
        setTimeout(() => {
          sendBtn.textContent = '🚀';
          sendBtn.classList.remove('sent');
          sendBtn.disabled = false;
        }, 1200);

        if (o.clearOnSend !== false) {
          input.value = '';
        }
      } catch (err) {
        sendBtn.textContent = '❌';
        toast(err.message || 'Ошибка отправки', 'err');
        setTimeout(() => {
          sendBtn.textContent = '🚀';
          sendBtn.disabled = false;
        }, 1500);
      }
    }

    sendBtn.onclick = (e) => {
      e.stopPropagation();
      doSend();
    };

    input.onkeydown = (e) => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        doSend();
      }
    };

    return {
      update() {},
      destroy() {
        alive = false;
        if (session && canListen) {
          bridge.cancelListening(session);
        }
      }
    };
  }
};
