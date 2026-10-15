import { useCallback, useImperativeHandle, useLayoutEffect, useRef, type Ref } from 'react'

export interface ZbEditorHandle { focus: () => void }

/** Metni parçalara böler: "ZB" işaretleri (ardından boşluk varsa), işaretin arkasındaki ifade, eklenmiş kriter cümleleri ve düz metin. */
type Part = { kind: 'text' | 'zb' | 'term' | 'criteria'; text: string; zbIndex?: number }

function tokenize(text: string, criteria: string[]): Part[] {
  const parts: Part[] = []
  const zb = /(^|\s)(zb)(?=\s)/gi
  const marks = [...text.matchAll(zb)].map(m => m.index! + m[1].length)
  let pos = 0
  const pushText = (t: string) => {
    if (!t) return
    // Eklenmiş kriter cümlelerini vurgula (en uzun önce)
    const sorted = [...criteria].filter(Boolean).sort((a, b) => b.length - a.length)
    let rest = t
    while (rest) {
      let best: { i: number; c: string } | null = null
      for (const c of sorted) {
        const i = rest.indexOf(c)
        if (i >= 0 && (!best || i < best.i)) best = { i, c }
      }
      if (!best) { parts.push({ kind: 'text', text: rest }); break }
      if (best.i > 0) parts.push({ kind: 'text', text: rest.slice(0, best.i) })
      parts.push({ kind: 'criteria', text: best.c })
      rest = rest.slice(best.i + best.c.length)
    }
  }
  marks.forEach((start, i) => {
    pushText(text.slice(pos, start))
    parts.push({ kind: 'zb', text: text.slice(start, start + 2), zbIndex: i })
    const end = i + 1 < marks.length ? marks[i + 1] : text.length
    const segment = text.slice(start + 2, end)
    const lead = segment.match(/^\s*/)![0]
    const trail = segment.match(/\s*$/)![0]
    const term = segment.slice(lead.length, segment.length - trail.length)
    if (lead) parts.push({ kind: 'text', text: lead })
    if (term) parts.push({ kind: 'term', text: term, zbIndex: i })
    if (trail && term) parts.push({ kind: 'text', text: trail })
    pos = end
  })
  pushText(text.slice(pos))
  return parts
}

const CLASSES: Record<Part['kind'], string> = {
  text: '',
  zb: 'mx-0.5 inline-block cursor-pointer select-none rounded-md bg-amber-100 px-1.5 font-bold tracking-wide text-amber-900 ring-1 ring-amber-300 hover:bg-amber-200',
  term: 'font-semibold text-amber-800 underline decoration-amber-400 decoration-dotted underline-offset-4',
  criteria: 'rounded bg-indigo-50 px-0.5 text-indigo-800 ring-1 ring-indigo-100',
}

function render(root: HTMLElement, text: string, criteria: string[]) {
  const nodes = tokenize(text, criteria).map(p => {
    if (p.kind === 'text') return document.createTextNode(p.text)
    const span = document.createElement('span')
    span.className = CLASSES[p.kind]
    span.textContent = p.text
    if (p.kind === 'zb') {
      span.contentEditable = 'false'
      span.dataset.zb = String(p.zbIndex)
      span.title = 'Kriter penceresini aç'
    }
    if (p.kind === 'term') span.dataset.term = String(p.zbIndex)
    return span
  })
  root.replaceChildren(...nodes)
}

/** Düz metin: ZB rozeti "ZB" olarak sayılır, tarayıcının eklediği bölünmez boşluklar normal boşluğa çevrilir. */
function textOf(root: Node): string {
  let out = ''
  root.childNodes.forEach(n => {
    if (n.nodeType === Node.TEXT_NODE) out += n.textContent ?? ''
    else if (n instanceof HTMLElement && n.dataset.zb !== undefined) out += 'ZB'
    else if (n instanceof HTMLBRElement) out += ''
    else out += textOf(n)
  })
  return out.replace(/ /g, ' ').replace(/\n/g, ' ')
}

function nodeLength(n: Node): number {
  if (n.nodeType === Node.TEXT_NODE) return n.textContent?.length ?? 0
  if (n instanceof HTMLElement && n.dataset.zb !== undefined) return 2
  let len = 0
  n.childNodes.forEach(c => { len += nodeLength(c) })
  return len
}

function caretOffset(root: HTMLElement): number | null {
  const sel = window.getSelection()
  if (!sel || sel.rangeCount === 0 || !root.contains(sel.anchorNode)) return null
  const range = sel.getRangeAt(0)
  let total = 0, done = false
  const walk = (node: Node) => {
    if (done) return
    if (node === range.startContainer) {
      if (node.nodeType === Node.TEXT_NODE) total += range.startOffset
      else for (let i = 0; i < range.startOffset; i++) total += nodeLength(node.childNodes[i])
      done = true
      return
    }
    if (node.nodeType === Node.TEXT_NODE || (node instanceof HTMLElement && node.dataset.zb !== undefined)) { total += nodeLength(node); return }
    node.childNodes.forEach(walk)
  }
  root.childNodes.forEach(walk)
  return done ? total : null
}

function setCaret(root: HTMLElement, offset: number) {
  const sel = window.getSelection()
  if (!sel) return
  const range = document.createRange()
  let remaining = offset
  for (const child of Array.from(root.childNodes)) {
    const len = nodeLength(child)
    if (remaining <= len) {
      if (child instanceof HTMLElement && child.dataset.zb !== undefined) range.setStartAfter(child)
      else {
        const textNode = child.nodeType === Node.TEXT_NODE ? child : child.firstChild ?? child
        range.setStart(textNode, Math.min(remaining, textNode.textContent?.length ?? 0))
      }
      range.collapse(true)
      sel.removeAllRanges()
      sel.addRange(range)
      return
    }
    remaining -= len
  }
  range.selectNodeContents(root)
  range.collapse(false)
  sel.removeAllRanges()
  sel.addRange(range)
}

/**
 * Soru kutusu. "ZB " yazılınca ZB kalın, çerçeveli bir rozete dönüşür ve arkasındaki ifade vurgulanır;
 * ZB penceresinden eklenen kriter cümleleri de renkli gösterilir. Değer her zaman düz metindir.
 */
export function ZbEditor({ value, criteria, onChange, onEnter, onZbClick, disabled, placeholder, ref }: {
  value: string
  criteria: string[]
  onChange: (text: string, caret: number | null) => void
  onEnter: () => void
  onZbClick: (zbIndex: number) => void
  disabled?: boolean
  placeholder: string
  ref?: Ref<ZbEditorHandle>
}) {
  const root = useRef<HTMLDivElement>(null)
  const last = useRef<string | null>(null)
  const composing = useRef(false)

  useImperativeHandle(ref, () => ({
    focus: () => { const el = root.current; if (el) { el.focus(); setCaret(el, textOf(el).length) } },
  }), [])

  // Dışarıdan gelen değişiklik (gönderince temizleme, kriter ekleme): yeniden çiz ve imleci sona al
  useLayoutEffect(() => {
    const el = root.current
    if (!el) return
    if (value !== last.current) {
      render(el, value, criteria)
      last.current = value
      if (document.activeElement === el) setCaret(el, value.length)
    }
  }, [value, criteria])

  const sync = useCallback(() => {
    const el = root.current
    if (!el || composing.current) return
    const caret = caretOffset(el)
    const text = textOf(el)
    last.current = text
    render(el, text, criteria)
    if (caret !== null) setCaret(el, caret)
    onChange(text, caret)
  }, [criteria, onChange])

  return (
    <div className="relative min-w-0 flex-1">
      <div ref={root} role="textbox" aria-label="Soru" aria-multiline="false" aria-disabled={disabled}
        contentEditable={!disabled} suppressContentEditableWarning spellCheck={false}
        onInput={sync}
        onCompositionStart={() => { composing.current = true }}
        onCompositionEnd={() => { composing.current = false; sync() }}
        onKeyDown={e => {
          if (e.key === 'Enter') { e.preventDefault(); if (!e.shiftKey) onEnter() }
        }}
        onPaste={e => {
          e.preventDefault()
          document.execCommand('insertText', false, e.clipboardData.getData('text/plain').replace(/\s+/g, ' '))
        }}
        onClick={e => {
          const zb = (e.target as HTMLElement).closest<HTMLElement>('[data-zb]')
          if (zb) { e.preventDefault(); onZbClick(Number(zb.dataset.zb)) }
        }}
        className={`max-h-36 min-h-[50px] overflow-y-auto whitespace-pre-wrap break-words rounded-xl border border-slate-300 bg-white px-4 py-3 leading-6 text-slate-900 outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100 ${disabled ? 'cursor-not-allowed bg-slate-50 text-slate-400' : ''}`} />
      {!value && (
        <div className="pointer-events-none absolute inset-0 truncate px-4 py-3 leading-6 text-slate-400" aria-hidden="true">{placeholder}</div>
      )}
    </div>
  )
}
