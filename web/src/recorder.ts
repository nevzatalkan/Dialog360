/**
 * Mikrofon kaydı → 16 kHz mono 16-bit PCM WAV. Kayıt tarayıcıda WAV'a çevrildiği için sunucuda ffmpeg gerekmez
 * ve aynı dosya hem whisper.cpp'ye hem Azure Speech'e gönderilebilir.
 */
const TARGET_RATE = 16000

export interface Recording {
  stop: () => Promise<Blob>
  cancel: () => void
}

/** Bu süreden kısa (ya da hiç) ses gelmediyse kayıt boş sayılır. */
const MIN_SECONDS = 0.3
/** En yüksek genlik bunun altındaysa kayıt sessiz sayılır. */
const SILENCE_PEAK = 0.01

export class EmptyRecordingError extends Error {}

export async function startRecording(): Promise<Recording> {
  // AudioContext tıklamayla aynı anda oluşturulur; izin beklenirken "askıya alınmış" başlamasın
  const ctx = new AudioContext()
  let stream: MediaStream
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } })
  } catch (e) {
    void ctx.close()
    throw e
  }
  if (ctx.state === 'suspended') await ctx.resume()
  const track = stream.getAudioTracks()[0]
  if (!track || track.readyState === 'ended') {
    stream.getTracks().forEach(t => t.stop())
    void ctx.close()
    throw new EmptyRecordingError('Mikrofondan ses alınamadı. Bu tarayıcı mikrofonu engelliyor olabilir; uygulamayı Chrome, Edge ya da Safari\'de açın.')
  }
  const source = ctx.createMediaStreamSource(stream)
  // ScriptProcessor eski ama her tarayıcıda çalışır ve ayrı bir worklet dosyası gerektirmez
  const processor = ctx.createScriptProcessor(4096, 1, 1)
  const chunks: Float32Array[] = []
  processor.onaudioprocess = e => chunks.push(new Float32Array(e.inputBuffer.getChannelData(0)))
  source.connect(processor)
  processor.connect(ctx.destination)

  const release = () => {
    processor.disconnect()
    source.disconnect()
    stream.getTracks().forEach(t => t.stop())
    void ctx.close()
  }

  return {
    stop: async () => {
      const rate = ctx.sampleRate
      release()
      const samples = merge(chunks)
      if (samples.length < rate * MIN_SECONDS)
        throw new EmptyRecordingError('Mikrofondan ses alınamadı. Mikrofon iznini kontrol edin; Claude uygulamasının içindeki tarayıcı mikrofonu engeller, Chrome/Edge/Safari kullanın.')
      // Tamamen sessiz kayıt: Whisper sessizlikte uydurma metin üretebilir ("Altyazı M.K." gibi)
      let peak = 0
      for (let i = 0; i < samples.length; i++) peak = Math.max(peak, Math.abs(samples[i]))
      if (peak < SILENCE_PEAK)
        throw new EmptyRecordingError('Kayıtta ses algılanmadı. Mikrofonun açık olduğundan emin olun ve biraz daha yüksek sesle konuşun.')
      return encodeWav(downsample(samples, rate, TARGET_RATE), TARGET_RATE)
    },
    cancel: release,
  }
}

function merge(chunks: Float32Array[]): Float32Array {
  const out = new Float32Array(chunks.reduce((n, c) => n + c.length, 0))
  let offset = 0
  for (const c of chunks) { out.set(c, offset); offset += c.length }
  return out
}

function downsample(input: Float32Array, from: number, to: number): Float32Array {
  if (from === to) return input
  const ratio = from / to
  const out = new Float32Array(Math.floor(input.length / ratio))
  for (let i = 0; i < out.length; i++) {
    // basit ortalama: örtüşmeyi (aliasing) azaltır
    const start = Math.floor(i * ratio), end = Math.min(Math.floor((i + 1) * ratio), input.length)
    let sum = 0
    for (let j = start; j < end; j++) sum += input[j]
    out[i] = sum / Math.max(1, end - start)
  }
  return out
}

function encodeWav(samples: Float32Array, rate: number): Blob {
  const buffer = new ArrayBuffer(44 + samples.length * 2)
  const v = new DataView(buffer)
  const text = (offset: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(offset + i, s.charCodeAt(i)) }
  text(0, 'RIFF'); v.setUint32(4, 36 + samples.length * 2, true); text(8, 'WAVE')
  text(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true)
  v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true)
  text(36, 'data'); v.setUint32(40, samples.length * 2, true)
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]))
    v.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true)
  }
  return new Blob([buffer], { type: 'audio/wav' })
}
