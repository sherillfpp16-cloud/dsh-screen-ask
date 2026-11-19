window.__ModuleLoader__.load({
  id: 'dsh-screen-ask',
  factory(require) {
    const React = require('react')
    const h = React.createElement
    const API = '/screen-ask'
    const { useState, useEffect, useRef, useCallback } = React

    const font = '13px/1.5 "Microsoft YaHei UI", system-ui, sans-serif'
    const styles = {
      launcher: {
        position: 'fixed', right: 20, bottom: 96, zIndex: 900,
        padding: '8px 12px', borderRadius: 10, cursor: 'pointer', userSelect: 'none',
        font, color: '#111111', background: '#ffffff',
        border: '1px solid rgba(0,0,0,0.16)',
        boxShadow: '0 6px 24px rgba(0,0,0,0.18)', backdropFilter: 'blur(8px)',
      },
      panel: {
        position: 'fixed', zIndex: 1000, width: 440, maxHeight: '76vh',
        display: 'flex', flexDirection: 'column',
        borderRadius: 12, overflow: 'hidden',
        font, color: '#111111', background: '#ffffff',
        border: '1px solid rgba(0,0,0,0.16)',
        boxShadow: '0 18px 60px rgba(0,0,0,0.30)',
      },
      head: {
        display: 'flex', alignItems: 'center', gap: 8, padding: '10px 12px',
        cursor: 'move', userSelect: 'none',
        borderBottom: '1px solid rgba(0,0,0,0.10)',
        background: '#f5f5f7', color: '#111111',
      },
      title: { fontWeight: 600, fontSize: 13, color: '#111111' },
      chip: { fontSize: 11, color: '#666666' },
      close: { border: 0, background: 'transparent', cursor: 'pointer', fontSize: 16, lineHeight: 1, color: '#333333', padding: '2px 4px' },
      body: { padding: 12, display: 'flex', flexDirection: 'column', gap: 10, overflow: 'auto', flex: 1, minHeight: 0 },
      preview: { width: '100%', maxHeight: 170, objectFit: 'contain', borderRadius: 8, border: '1px solid rgba(0,0,0,0.14)', background: '#f2f2f4' },
      hint: { fontSize: 12, color: '#666666' },
      textarea: {
        width: '100%', minHeight: 54, resize: 'vertical', borderRadius: 8, padding: '8px 10px',
        font, color: '#111111', background: '#ffffff', border: '1px solid rgba(0,0,0,0.20)', outline: 'none',
      },
      row: { display: 'flex', gap: 8, alignItems: 'center' },
      select: {
        flex: 1, padding: '6px 8px', borderRadius: 8, font, color: '#111111',
        background: '#ffffff', border: '1px solid rgba(0,0,0,0.20)',
      },
      button: { padding: '7px 14px', borderRadius: 8, border: 0, cursor: 'pointer', fontWeight: 600, color: '#ffffff', background: '#3b7ddd' },
      answer: {
        whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: '34vh', overflow: 'auto',
        padding: '8px 10px', borderRadius: 8, background: '#f3f4f6', color: '#111111', fontSize: 13,
      },
      error: { color: '#c62828', fontSize: 12, whiteSpace: 'pre-wrap' },
      resize: {
        position: 'absolute', right: 0, bottom: 0, width: 16, height: 16,
        cursor: 'nwse-resize', touchAction: 'none',
        background: 'linear-gradient(135deg, transparent 45%, rgba(0,0,0,0.28) 45%, rgba(0,0,0,0.28) 55%, transparent 55%)',
        borderRadius: '0 0 12px 0',
      },
      min: { border: 0, background: 'transparent', cursor: 'pointer', fontSize: 15, lineHeight: 1, color: '#333333', padding: '2px 4px' },
    }

    function ScreenAsk() {
      const [open, setOpen] = useState(false)
      const [state, setState] = useState(null)
      const [question, setQuestion] = useState('')
      const [effort, setEffort] = useState('')
      const [answer, setAnswer] = useState('')
      const [error, setError] = useState('')
      const [busy, setBusy] = useState(false)
      const [pos, setPos] = useState({ x: Math.max(16, window.innerWidth - 470), y: 88 })
      const [size, setSize] = useState({ w: 440, h: 0 })
      const [seenTs, setSeenTs] = useState(0)
      const drag = useRef(null)
      const resize = useRef(null)

      const refresh = useCallback(async () => {
        try {
          const response = await fetch(`${API}/state`, { cache: 'no-store' })
          if (!response.ok) return
          const next = await response.json()
          setState(next)
          if (next.capture && next.capture.ts > (seenTs || 0) && next.capture.ts > (next.dismissedTs || 0)) {
            // The desktop panel (native window) handles new captures; the page
            // overlay stays available through the launcher chip.
            setSeenTs(next.capture.ts)
          }
        } catch {
          /* host not reachable yet */
        }
      }, [seenTs])

      useEffect(() => {
        refresh()
        const timer = setInterval(refresh, 1500)
        return () => clearInterval(timer)
      }, [refresh])

      useEffect(() => {
        if (!effort && state && state.reasoningEffort) setEffort(state.reasoningEffort)
      }, [state, effort])

      const onPointerDown = (event) => {
        if (event.button !== 0) return
        const target = event.target
        if (target && target.closest && target.closest('button')) return
        drag.current = { dx: event.clientX - pos.x, dy: event.clientY - pos.y }
        event.currentTarget.setPointerCapture(event.pointerId)
      }
      const onPointerMove = (event) => {
        if (!drag.current) return
        setPos({
          x: Math.min(window.innerWidth - 120, Math.max(0, event.clientX - drag.current.dx)),
          y: Math.min(window.innerHeight - 60, Math.max(0, event.clientY - drag.current.dy)),
        })
      }
      const onPointerUp = () => { drag.current = null }

      const onResizeDown = (event) => {
        if (event.button !== 0) return
        const panel = event.currentTarget.parentElement
        resize.current = {
          w: size.w,
          h: size.h || (panel ? panel.offsetHeight : 480),
          x: event.clientX,
          y: event.clientY,
        }
        event.currentTarget.setPointerCapture(event.pointerId)
      }
      const onResizeMove = (event) => {
        if (!resize.current) return
        const w = Math.min(window.innerWidth - 40, Math.max(300, resize.current.w + (event.clientX - resize.current.x)))
        const h = Math.min(window.innerHeight - 80, Math.max(240, resize.current.h + (event.clientY - resize.current.y)))
        setSize({ w, h })
      }
      const onResizeUp = () => { resize.current = null }

      const close = () => {
        setOpen(false)
        if (state && state.capture) {
          fetch(`${API}/dismiss`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ ts: state.capture.ts }),
          }).catch(() => {})
        }
      }

      const ask = async () => {
        setBusy(true)
        setError('')
        setAnswer('')
        const href = String(window.location.href || '')
        const matched = href.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)
        const sessionId = matched ? matched[0] : ''
        try {
          const response = await fetch(`${API}/ask`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ question, effort, sessionId }),
          })
          const payload = await response.json().catch(() => ({}))
          if (!response.ok) setError(payload.error || `请求失败 (${response.status})`)
          else setAnswer(payload.answer || '(空回答)')
        } catch (e) {
          setError(`请求失败: ${e && e.message ? e.message : e}`)
        } finally {
          setBusy(false)
        }
      }

      if (!open) {
        return h('div', { style: styles.launcher, onClick: () => setOpen(true), title: '打开屏幕问答' }, '截屏问答')
      }

      const helperText = state ? ({
        ready: '助手就绪', starting: '助手启动中', down: '助手未运行',
      }[state.helper] || state.helper) : '连接中'
      const capture = state && state.capture
      const options = state && state.efforts && state.efforts.length ? state.efforts : ['off']

      return h('div', { style: { ...styles.panel, left: pos.x, top: pos.y, width: size.w, height: size.h || 'auto', maxHeight: 'none' } },
        h('div', {
          style: styles.head,
          onPointerDown,
          onPointerMove,
          onPointerUp,
          onDoubleClick: (event) => {
            const target = event.target
            if (target && target.closest && target.closest('button')) return
            setOpen(false)
          },
        },
          h('span', { style: styles.title }, '屏幕问答'),
          h('span', { style: styles.chip }, helperText + (state && state.model ? ` · ${state.model}` : '')),
          h('button', { style: styles.min, onClick: () => setOpen(false), title: '收起' }, '—'),
          h('button', { style: styles.close, onClick: close, title: '关闭' }, '×'),
        ),
        h('div', { style: styles.body },
          capture
            ? h('img', { style: styles.preview, src: `${API}/image?ts=${capture.ts}`, alt: '截图预览' })
            : h('div', { style: styles.hint }, '还没截图：按住 X 键不放，再用左键拖一个矩形；松开 X 或右键/Esc 取消。'),
          h('textarea', {
            style: styles.textarea,
            placeholder: '想问什么？（可留空，默认让它解释截图内容）',
            value: question,
            onChange: (event) => setQuestion(event.target.value),
          }),
          h('div', { style: styles.row },
            h('select', {
              style: styles.select,
              value: effort,
              onChange: (event) => setEffort(event.target.value),
              title: '本次解答的思考程度',
            }, options.map((id) => h('option', { key: id, value: id }, `思考: ${id}`))),
            h('button', { style: { ...styles.button, opacity: busy ? 0.6 : 1 }, disabled: busy, onClick: ask }, busy ? '思考中…' : '解答'),
          ),
          error ? h('div', { style: styles.error }, error) : null,
          answer ? h('div', { style: styles.answer }, answer) : null,
          state && state.lastError ? h('div', { style: styles.error }, state.lastError) : null,
        ),
        h('div', {
          style: styles.resize,
          onPointerDown: onResizeDown,
          onPointerMove: onResizeMove,
          onPointerUp: onResizeUp,
          title: '拖动调整大小',
        }),
      )
    }

    function ScreenAskSettings() {
      const [hotkey, setHotkey] = useState('')
      const [status, setStatus] = useState('')
      const [busy, setBusy] = useState(false)

      useEffect(() => {
        let alive = true
        fetch(`${API}/hotkey`, { cache: 'no-store' })
          .then((response) => (response.ok ? response.json() : null))
          .then((data) => { if (alive && data && typeof data.hotkey === 'string') setHotkey(data.hotkey) })
          .catch(() => {})
        return () => { alive = false }
      }, [])

      const save = async () => {
        setBusy(true)
        setStatus('')
        try {
          const response = await fetch(`${API}/hotkey`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ hotkey }),
          })
          const payload = await response.json().catch(() => ({}))
          if (!response.ok) setStatus(payload.error || `保存失败 (${response.status})`)
          else setStatus('已保存，立即生效')
        } catch (e) {
          setStatus(`保存失败: ${e && e.message ? e.message : e}`)
        } finally {
          setBusy(false)
        }
      }

      return h('section', {
        style: { padding: '14px 16px', maxWidth: 520, display: 'flex', flexDirection: 'column', gap: 8 },
        'data-testid': 'dsh-screen-ask-settings',
      },
        h('h3', { style: { margin: 0, fontSize: 15 } }, '屏幕问答'),
        h('p', { style: { margin: 0, color: '#666', fontSize: 12 } },
          '截图快捷键：按住它，再用左键拖拽框选区域。例如 x、f9、ctrl+shift+x、side（鼠标侧键）。'),
        h('div', { style: { display: 'flex', gap: 8, alignItems: 'center' } },
          h('input', {
            value: hotkey,
            onChange: (event) => setHotkey(event.target.value),
            placeholder: 'x',
            style: { flex: 1, padding: '7px 10px', borderRadius: 8, border: '1px solid rgba(0,0,0,0.2)', fontSize: 13, color: '#111', background: '#fff' },
          }),
          h('button', {
            onClick: save,
            disabled: busy,
            style: { padding: '7px 16px', borderRadius: 8, border: 0, background: '#3b7ddd', color: '#fff', cursor: 'pointer', fontWeight: 600 },
          }, busy ? '保存中…' : '保存'),
        ),
        status ? h('div', { style: { fontSize: 12, color: status.startsWith('已保存') ? '#17803d' : '#c62828' } }, status) : null,
      )
    }

    function registerSettings(ctx) {
      const slots = ['settings.plugins.tab', 'plugins.bundle.config', 'plugins.row.config', 'settings.plugin.item']
      for (const slot of slots) {
        try {
          ctx.slots.inject(slot, () => {
            try {
              ctx.slots.register({
                name: slot,
                key: 'dsh-screen-ask',
                id: 'dsh-screen-ask',
                order: 40,
                label: () => '屏幕问答',
                inject: () => ({}),
              }, ScreenAskSettings)
            } catch { /* slot shape changed; keep the rest working */ }
          })
        } catch { /* slot unavailable on this build */ }
      }
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        ctx.slots.inject('shell.overlay', () => ctx.slots.register({
          name: 'shell.overlay',
          id: 'screen-ask-panel',
          order: 960,
        }, ScreenAsk))
        registerSettings(ctx)
      },
    }
  },
})
