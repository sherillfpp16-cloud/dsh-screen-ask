/**
 * dsh-screen-ask — Host half.
 *
 * The capture helper is a GUI-subsystem .NET program (no console): it listens
 * for a mouse side button, shows a full-screen selection overlay and appends
 * JSON events plus PNG files into its output directory. This plugin tails that
 * event file, serves a small JSON API to the Client panel, and streams one
 * model answer for the captured image.
 */
import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, openSync, readSync, closeSync, writeFileSync, appendFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { randomUUID } from 'node:crypto'
import os from 'node:os'

export const name = 'screen-ask'
export const inject = ['webServer', 'llm', 'attachments', 'sessions']

const HERE = dirname(fileURLToPath(import.meta.url))
const HELPER = join(HERE, 'helper', 'ScreenSnap.exe')
const BOOTSTRAP = join(HERE, 'helper', 'Bootstrap.exe')
const PANEL = join(HERE, 'helper', 'Panel.exe')
const BASE = '/screen-ask'
const EVENTS_FILE = 'events.jsonl'

function json(res, status, value) {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(JSON.stringify(value))
}

function isLoopback(req) {
  const address = req.socket && req.socket.remoteAddress
  return address === '127.0.0.1' || address === '::1' || address === '::ffff:127.0.0.1'
}

function readJson(req, limit = 256 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0
    const chunks = []
    req.on('data', (chunk) => {
      size += chunk.length
      if (size > limit) { reject(new Error('payload too large')); req.destroy(); return }
      chunks.push(chunk)
    })
    req.on('end', () => {
      try { resolve(chunks.length === 0 ? {} : JSON.parse(Buffer.concat(chunks).toString('utf8'))) }
      catch (error) { reject(error) }
    })
    req.on('error', reject)
  })
}

function latestShot(dir) {
  try {
    const names = readdirSync(dir).filter((n) => n.startsWith('snap-') && n.endsWith('.png'))
    let best = null
    let bestAt = 0
    for (const n of names) {
      const p = join(dir, n)
      const t = statSync(p).mtimeMs
      if (t > bestAt) { bestAt = t; best = p }
    }
    return best
  } catch { return null }
}

export function apply(ctx, config) {
  const cfg = {
    sideButton: 1,
    provider: '',
    model: '',
    reasoningEffort: 'off',
    holdKey: 'x',
    outDir: '',
    writeBackSession: true,
    efforts: [],
    prompt: '请阅读这张截图：说明它是什么、关键内容有哪些；如果是题目或报错，请给出答案/解决方案和简要依据。回答用中文。',
    ...(config || {}),
  }

  const outDir = cfg.outDir || join(process.env.USERPROFILE || os.homedir(), 'Documents', 'dsh-screen-ask')
  try { mkdirSync(outDir, { recursive: true }) } catch { /* ignore */ }
  const eventsPath = join(outDir, EVENTS_FILE)

  const state = {
    helper: 'starting',
    button: cfg.sideButton,
    holdKey: cfg.holdKey,
    hotkey: cfg.holdKey,
    provider: cfg.provider,
    model: cfg.model,
    reasoningEffort: cfg.reasoningEffort,
    efforts: [],
    capture: null,
    dismissedTs: 0,
    lastError: '',
  }

  let child = null
  let disposed = false
  let restartTimer = null
  let watchTimer = null
  let eventOffset = 0
  let catalog = null
  let effortsKnown = false
  if (Array.isArray(cfg.efforts) && cfg.efforts.length > 0) {
    state.efforts = cfg.efforts.map((e) => String(e)).filter(Boolean)
    effortsKnown = state.efforts.length > 0
    if (!state.efforts.includes(state.reasoningEffort)) state.reasoningEffort = state.efforts[0]
  }

  function logAsk(line) {
    try { appendFileSync(join(outDir, 'ask.log'), new Date().toISOString() + ' ' + line + '\n') } catch { /* diagnostics only */ }
  }

  function pickSession(hint) {
    try {
      const sessions = typeof ctx.sessions?.list === 'function' ? ctx.sessions.list() : []
      if (!Array.isArray(sessions) || sessions.length === 0) return null
      if (hint) {
        const found = sessions.find((s) => String(s.id ?? '') === String(hint))
        if (found) return found
      }
      return sessions[sessions.length - 1]
    } catch (error) {
      logAsk(`pick-session error ${error instanceof Error ? error.message : String(error)}`)
      return null
    }
  }

  function writeBack(hintSessionId, imageRef, question, answer) {
    if (!cfg.writeBackSession) return 'disabled'
    try {
      const session = pickSession(hintSessionId)
      if (!session || typeof session.append !== 'function') return 'no-session'
      const note = `【屏幕问答】${question ? `问题：${question}\n` : ''}解答：${answer}`
      const content = [{ type: 'text', text: note }]
      if (imageRef) content.push({ type: 'image', attachment: imageRef })
      session.append('user/message', {
        role: 'user',
        content,
        source: { kind: 'user' },
      }, { surfaceOp: 'append' })
      return `ok:${String(session.id ?? '')}`
    } catch (error) {
      return `error:${error instanceof Error ? error.message : String(error)}`
    }
  }
  let capturePath = null

  function resetOffset() {
    try { eventOffset = statSync(eventsPath).size } catch { eventOffset = 0 }
  }

  function drainEvents() {
    let size
    try { size = statSync(eventsPath).size } catch { return }
    if (size < eventOffset) eventOffset = 0
    if (size === eventOffset) return
    const length = size - eventOffset
    const buffer = Buffer.alloc(length)
    let fd
    try {
      fd = openSync(eventsPath, 'r')
      readSync(fd, buffer, 0, length, eventOffset)
    } catch (error) {
      state.lastError = `读取截图事件失败: ${error.message}`
      return
    } finally {
      if (fd !== undefined) { try { closeSync(fd) } catch { /* ignore */ } }
    }
    eventOffset = size
    const text = buffer.toString('utf8')
    for (const line of text.split('\n')) {
      const trimmed = line.trim()
      if (!trimmed) continue
      let event
      try { event = JSON.parse(trimmed) } catch { continue }
      if (event.type === 'ready') {
        state.helper = 'ready'
        if (typeof event.hotkey === 'string' && event.hotkey) state.hotkey = event.hotkey
        state.button = Number(event.button) || cfg.sideButton
        if (typeof event.hold === 'string' && event.hold) state.holdKey = event.hold
      } else if (event.type === 'capture') {
        capturePath = typeof event.path === 'string' ? event.path : null
        state.capture = { ts: Date.now(), w: Number(event.w) || 0, h: Number(event.h) || 0 }
        state.dismissedTs = 0
        state.lastError = ''
        state.helper = 'ready'
      } else if (event.type === 'error') {
        state.lastError = String(event.message || '助手错误')
      }
    }
  }

  async function resolveCatalog() {
    if (catalog) return catalog
    let provider = cfg.provider
    let model = cfg.model
    try {
      if (!provider && typeof ctx.llm.listProviders === 'function') {
        const providers = await ctx.llm.listProviders()
        if (Array.isArray(providers) && providers.length) {
          const first = providers[0]
          provider = typeof first === 'string' ? first : String(first.name ?? first.id ?? '')
        }
      }
      if (provider && typeof ctx.llm.listModels === 'function') {
        const models = await ctx.llm.listModels(provider)
        if (Array.isArray(models) && models.length) {
          logAsk(`models[0]=${JSON.stringify(models[0]).slice(0, 700)}`)
          const picked = (model ? models.find((m) => String(m.id ?? m.name) === model) : null) || models[0]
          if (!model) model = String(picked.id ?? picked.name ?? '')
          const efforts = picked.reasoning && picked.reasoning.efforts
          if (Array.isArray(efforts)) {
            state.efforts = efforts
              .map((e) => (typeof e === 'string' ? e : String(e.id ?? e.name ?? e)))
              .filter(Boolean)
            effortsKnown = state.efforts.length > 0
          }
        }
      }
    } catch (error) {
      state.lastError = `读取模型目录失败: ${error instanceof Error ? error.message : String(error)}`
    }
    state.provider = provider || state.provider
    state.model = model || state.model
    if (state.efforts.length === 0) state.efforts = ['off']
    if (!state.efforts.includes(state.reasoningEffort)) state.reasoningEffort = state.efforts[0]
    catalog = { provider: state.provider, model: state.model }
    return catalog
  }

  function logPlugin(line) {
    try { appendFileSync(join(outDir, 'plugin.log'), new Date().toISOString() + ' ' + line + '\n') } catch { /* diagnostics only */ }
  }

  function startHelper() {
    // The helper must run outside any low-integrity sandbox to receive global
    // input hooks. ScreenSnap.exe carries a medium integrity label, so even a
    // sandboxed parent launches it at the user's level; the exe hides its own
    // console window.
    if (!existsSync(HELPER)) {
      state.helper = 'down'
      state.lastError = `缺少截图助手: ${HELPER}`
      logPlugin('helper missing: ' + HELPER)
      return
    }
    try {
      const args = [
        '--out', outDir,
        '--hotkey', String(cfg.holdKey),
        '--button', String(cfg.sideButton),
        '--heartbeat', join(outDir, 'heartbeat'),
      ]
      logPlugin('spawn ' + HELPER + ' ' + args.join(' '))
      const child = spawn(HELPER, args, { stdio: 'ignore', windowsHide: true, detached: true })
      child.on('error', (error) => {
        state.lastError = `助手启动失败: ${error.message}`
        logPlugin('spawn error: ' + error.message)
      })
      child.unref()
      state.helper = 'starting'
      logPlugin('spawned pid=' + child.pid)
    } catch (error) {
      state.helper = 'down'
      state.lastError = `助手启动失败: ${error instanceof Error ? error.message : String(error)}`
      logPlugin('spawn throw: ' + state.lastError)
    }
  }

  function stopHelper() {
    disposed = true
    if (watchTimer) clearInterval(watchTimer)
    if (heartbeatTimer) clearInterval(heartbeatTimer)
    if (panelTimer) clearInterval(panelTimer)
    watchTimer = null
    heartbeatTimer = null
    panelTimer = null
    state.helper = 'down'
  }

  function hotkeyFile() { return join(outDir, 'hotkey.txt') }
  function readHotkey() {
    try {
      const value = readFileSync(hotkeyFile(), 'utf8').trim()
      if (value) return value
    } catch { /* not configured yet */ }
    return String(cfg.holdKey || 'x')
  }
  function writeHotkey(value) {
    writeFileSync(hotkeyFile(), value, 'utf8')
    state.hotkey = value
  }
  function normalizeHotkey(value) {
    if (typeof value !== 'string') return null
    const text = value.trim().toLowerCase().replace(/\s+/g, '')
    if (text.length === 0 || text.length > 40) return null
    if (!/^[a-z0-9+`~\-=\[\]\\;'.,/]+$/.test(text)) return null
    return text
  }

  function touchHeartbeat() {
    try { writeFileSync(join(outDir, 'heartbeat'), String(Date.now())) } catch { /* diagnostics only */ }
  }

  let heartbeatTimer = null
  let panelTimer = null

  async function answerForCapture(question, effort, hintSessionId) {
    const file = capturePath && existsSync(capturePath) ? capturePath : latestShot(outDir)
    if (!file) throw new Error('还没有截图：按住 X 拖动鼠标框选')
    const { provider, model } = await resolveCatalog()
    if (!provider || !model) throw new Error('没有可用的模型路由')
    const useEffort = effortsKnown && effort ? effort : undefined
    const data = readFileSync(file)
    const refs = await ctx.attachments.saveImages([{
      data: new Uint8Array(data),
      mediaType: 'image/png',
      name: 'screen-ask.png',
    }])
    const ref = Array.isArray(refs) ? refs[0] : refs
    const text = question ? `${cfg.prompt}\n\n用户的问题：${question}` : cfg.prompt
    const messages = [{
      id: randomUUID(),
      role: 'user',
      source: { kind: 'user' },
      content: [
        { type: 'text', text },
        { type: 'image', attachment: ref },
      ],
    }]
    let answer = ''
    let failure = null
    for await (const chunk of ctx.llm.stream({
      provider,
      model,
      messages,
      reasoningEffort: useEffort,
      signal: AbortSignal.timeout(120000),
    })) {
      if (!chunk) continue
      const kind = String(chunk.kind ?? chunk.type ?? '')
      if (kind === 'text-delta' || kind === 'text' || kind === 'delta' || kind === 'content') {
        const piece = chunk.text ?? chunk.delta ?? chunk.content
        if (typeof piece === 'string') answer += piece
        else if (Array.isArray(piece)) answer += piece.filter((p) => typeof p === 'string').join('')
      } else if (kind === 'error') {
        failure = chunk.failure || chunk.error || chunk
        break
      } else if (kind === 'aborted') {
        failure = chunk.failure || { code: 'ABORTED' }
        break
      } else if (kind === 'finish') {
        break
      }
    }
    if (failure) throw new Error(`模型调用失败: ${failure.code || failure.kind || 'LLM_FAILED'}`)
    const wrote = writeBack(hintSessionId, ref, question, answer)
    logAsk(`answer ok provider=${provider} model=${model} effort=${useEffort || '(default)'} chars=${answer.length} write-back ${wrote}`)
    return { answer, provider, model, effort: useEffort }
  }

  let panelRequestId = ''
  async function checkPanel() {
    let text
    try { text = readFileSync(join(outDir, 'panel-request.txt'), 'utf8') } catch { return }
    const lines = text.split('\n')
    const id = (lines[0] || '').trim()
    if (!id || id === panelRequestId) return
    panelRequestId = id
    const question = (lines[1] || '').trim().slice(0, 4000)
    const file = (lines[2] || '').trim()
    const effort = (lines[3] || '').trim()
    if (file && existsSync(file)) capturePath = file
    logAsk(`panel ask id=${id} effort=${effort || '(default)'}`)
    try {
      const result = await answerForCapture(question, effort, '')
      writeFileSync(join(outDir, 'panel-answer.txt'), `${id}\nOK\n${result.answer}`, 'utf8')
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      logAsk(`panel error ${message}`)
      writeFileSync(join(outDir, 'panel-answer.txt'), `${id}\nERR\n${message}`, 'utf8')
    }
  }

  function startPanel() {
    if (!existsSync(PANEL)) return
    try {
      const child = spawn(PANEL, [outDir], { stdio: 'ignore', windowsHide: true, detached: true })
      child.on('error', () => {})
      child.unref()
    } catch { /* the helper also starts it after a capture */ }
  }

  ctx.effect(() => {
    try {
      if (!existsSync(hotkeyFile())) writeHotkey(String(cfg.holdKey || 'x'))
      else state.hotkey = readHotkey()
    } catch { /* diagnostics only */ }
    touchHeartbeat()
    startHelper()
    startPanel()
    heartbeatTimer = setInterval(touchHeartbeat, 5000)
    watchTimer = setInterval(drainEvents, 350)
    panelTimer = setInterval(() => { checkPanel().catch(() => {}) }, 300)
    return () => stopHelper()
  }, 'screen-ask: capture helper')

  ctx.effect(() => ctx.webServer.register({
    kind: 'exact',
    path: `${BASE}/state`,
    handler: async (req, res) => {
      if (!isLoopback(req)) return json(res, 403, { error: 'local access only' })
      await resolveCatalog()
      try {
        writeFileSync(join(outDir, 'plugin-state.json'), JSON.stringify({
          provider: state.provider,
          model: state.model,
          efforts: state.efforts,
          helper: state.helper,
          holdKey: state.holdKey,
          lastError: state.lastError,
        }, null, 2))
      } catch { /* diagnostics only */ }
      json(res, 200, {
        helper: state.helper,
        button: state.button,
        holdKey: state.holdKey,
        provider: state.provider,
        model: state.model,
        efforts: state.efforts,
        reasoningEffort: state.reasoningEffort,
        capture: state.capture ? { ts: state.capture.ts, w: state.capture.w, h: state.capture.h } : null,
        dismissedTs: state.dismissedTs,
        hotkey: state.hotkey,
        lastError: state.lastError,
      })
    },
  }), 'screen-ask: state route')

  ctx.effect(() => ctx.webServer.register({
    kind: 'exact',
    path: `${BASE}/image`,
    handler: async (req, res) => {
      if (!isLoopback(req)) return json(res, 403, { error: 'local access only' })
      const file = capturePath && existsSync(capturePath) ? capturePath : latestShot(outDir)
      if (!file) return json(res, 404, { error: 'no capture' })
      try {
        const bytes = readFileSync(file)
        res.writeHead(200, { 'content-type': 'image/png', 'cache-control': 'no-store' })
        res.end(bytes)
      } catch (error) {
        state.lastError = error instanceof Error ? error.message : String(error)
        json(res, 500, { error: state.lastError })
      }
    },
  }), 'screen-ask: image route')

  ctx.effect(() => ctx.webServer.register({
    kind: 'exact',
    path: `${BASE}/hotkey`,
    handler: async (req, res) => {
      if (!isLoopback(req)) return json(res, 403, { error: 'local access only' })
      if (req.method === 'GET') return json(res, 200, { hotkey: readHotkey() })
      if (req.method !== 'POST') return json(res, 405, { error: 'method not allowed' })
      const body = await readJson(req).catch(() => ({}))
      const value = normalizeHotkey(body && body.hotkey)
      if (!value) return json(res, 400, { error: '快捷键格式不对' })
      try {
        writeHotkey(value)
        logAsk(`hotkey set: ${value}`)
        json(res, 200, { hotkey: value })
      } catch (error) {
        json(res, 500, { error: error instanceof Error ? error.message : String(error) })
      }
    },
  }), 'screen-ask: hotkey route')

  ctx.effect(() => ctx.webServer.register({
    kind: 'exact',
    path: `${BASE}/dismiss`,
    handler: async (req, res) => {
      if (!isLoopback(req)) return json(res, 403, { error: 'local access only' })
      const body = await readJson(req).catch(() => ({}))
      state.dismissedTs = Number(body.ts) || Date.now()
      json(res, 200, { ok: true })
    },
  }), 'screen-ask: dismiss route')

  ctx.effect(() => ctx.webServer.register({
    kind: 'exact',
    path: `${BASE}/ask`,
    handler: async (req, res) => {
      if (req.method !== 'POST') return json(res, 405, { error: 'method not allowed' })
      if (!isLoopback(req)) return json(res, 403, { error: 'local access only' })
      const started = Date.now()
      try {
        const body = await readJson(req)
        const question = typeof body.question === 'string' ? body.question.slice(0, 4000) : ''
        const effort = typeof body.effort === 'string' && body.effort ? body.effort : state.reasoningEffort
        const result = await answerForCapture(question, effort, body && typeof body.sessionId === 'string' ? body.sessionId : '')
        json(res, 200, { ...result, elapsedMs: Date.now() - started })
      } catch (error) {
        state.lastError = error instanceof Error ? error.message : String(error)
        logAsk(`ask error ${state.lastError}`)
        json(res, 400, { error: state.lastError })
      }
    },
  }), 'screen-ask: ask route')
}
