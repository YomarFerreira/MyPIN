"""
Gera os toques 1 a 4 e 10 a 12 dos lembretes do MyPIN (src/MyPIN/Sons/Toque N.wav).
O Toque 1 (corda dedilhada) é o padrão; 10, 11 e 12 são os mais agudos, por isso ficam no fim.

São sons originais, sintetizados aqui, nota por nota. Seguem o "jeito" de alarme do Windows
(um motivo curto tocado duas vezes, com pausa), mas com melodias e timbres próprios, para o
MyPIN poder ser distribuído sem levar arquivos de som da Microsoft.

Uso:  python tools/gerar_toques.py
Só usa a biblioteca padrão do Python (sem numpy).
"""
import math
import os
import random
import struct
import wave

SR = 22050
OUT = os.path.join(os.path.dirname(__file__), '..', 'src', 'MyPIN', 'Sons')
NOTE = {n: i for i, n in enumerate(['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'])}


def freq(name):
    """'A4' -> 440 Hz."""
    pitch, octave = name[:-1], int(name[-1])
    return 440.0 * 2 ** ((NOTE[pitch] + 12 * (octave + 1) - 69) / 12)


# ---------------- timbres: cada um devolve a amostra no tempo t (s) de uma nota ----------------

def epiano(f, t):
    """Piano elétrico suave (FM): brilho no ataque que vai sumindo."""
    idx = 1.8 * math.exp(-t * 6)
    v = math.sin(2 * math.pi * f * t + idx * math.sin(2 * math.pi * f * t))
    return v * math.exp(-t * 2.2) * min(1, t * 200)


def glock(f, t):
    """Glockenspiel: parciais agudas que somem rápido."""
    v = (math.sin(2 * math.pi * f * t) * math.exp(-t * 3.0)
         + 0.35 * math.sin(2 * math.pi * f * 2.76 * t) * math.exp(-t * 9)
         + 0.15 * math.sin(2 * math.pi * f * 5.40 * t) * math.exp(-t * 18))
    return v * min(1, t * 400)


def bell(f, t):
    """Sino: parciais levemente desafinadas, cauda longa."""
    parts = [(1.0, 1.0, 1.6), (2.0, 0.5, 2.4), (2.42, 0.35, 3.2), (3.0, 0.25, 4.0), (4.07, 0.18, 5.5)]
    v = sum(a * math.sin(2 * math.pi * f * r * t) * math.exp(-t * d) for r, a, d in parts)
    return v / 2.1 * min(1, t * 300)


def marimba(f, t):
    """Marimba: som de madeira, curto e redondo."""
    v = (math.sin(2 * math.pi * f * t) * math.exp(-t * 5.5)
         + 0.25 * math.sin(2 * math.pi * f * 4.0 * t) * math.exp(-t * 22)
         + 0.08 * math.sin(2 * math.pi * f * 9.9 * t) * math.exp(-t * 40))
    return v * min(1, t * 250)


def kalimba(f, t):
    """Kalimba: ataque metálico e corpo suave."""
    v = (math.sin(2 * math.pi * f * t) * math.exp(-t * 3.2)
         + 0.3 * math.sin(2 * math.pi * f * 5.95 * t) * math.exp(-t * 14))
    return v * min(1, t * 350)


def chime(f, t):
    """Carrilhão de vento: brilhante, com leve batimento."""
    v = (math.sin(2 * math.pi * f * t) + 0.6 * math.sin(2 * math.pi * f * 1.003 * t)) * math.exp(-t * 2.0)
    v += 0.3 * math.sin(2 * math.pi * f * 3.01 * t) * math.exp(-t * 6)
    return v / 1.7 * min(1, t * 400)


def pluck_note(f, dur, seed):
    """Corda dedilhada (Karplus-Strong), já como lista de amostras."""
    rnd = random.Random(seed)
    n = max(2, int(SR / f))
    buf = [rnd.uniform(-1, 1) for _ in range(n)]
    out = []
    for i in range(int(dur * SR)):
        v = buf[i % n]
        buf[i % n] = 0.996 * 0.5 * (v + buf[(i + 1) % n])
        out.append(v)
    return out


# ---------------- montagem ----------------

def render(events, total, timbre=None, tail=1.6, echo=(0.18, 0.28)):
    """events: lista de (início s, nota, volume). Devolve lista de amostras (float)."""
    buf = [0.0] * int(total * SR)
    for k, (start, note, vol) in enumerate(events):
        f = freq(note)
        s0 = int(start * SR)
        if timbre is None:  # corda dedilhada
            samples = pluck_note(f, tail, seed=k)
            for i, v in enumerate(samples):
                if s0 + i < len(buf):
                    buf[s0 + i] += v * vol
            continue
        for i in range(int(tail * SR)):
            if s0 + i >= len(buf):
                break
            buf[s0 + i] += timbre(f, i / SR) * vol
    # eco discreto (dá "ambiente" sem embolar)
    if echo:
        delay, gain = int(echo[0] * SR), echo[1]
        for i in range(delay, len(buf)):
            buf[i] += buf[i - delay] * gain
    return buf


def motif_twice(notes, step, gap, start=0.15, vol=0.8, accent_last=True):
    """Toca o motivo, pausa, e toca de novo (como os alarmes do Windows)."""
    ev = []
    for rep in range(2):
        t0 = start + rep * (len(notes) * step + gap)
        for j, n in enumerate(notes):
            ev.append((t0 + j * step, n, vol * (1.1 if accent_last and j == len(notes) - 1 else 1.0)))
    return ev


def save(name, buf, peak=0.85):
    m = max(1e-9, max(abs(v) for v in buf))
    fade = int(0.25 * SR)  # fim suave, sem estalo
    for i in range(fade):
        buf[-1 - i] *= i / fade
    data = b''.join(struct.pack('<h', int(max(-1, min(1, v / m * peak)) * 32767)) for v in buf)
    path = os.path.join(OUT, name)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data)
    print(f'{name}: {len(buf) / SR:.1f} s')


def main():
    os.makedirs(OUT, exist_ok=True)

    # 1 — "Amanhecer": arpejo de Fá maior subindo e voltando, piano elétrico (calmo, médio)
    ev = motif_twice(['F4', 'A4', 'C5', 'F5', 'E5', 'C5'], 0.22, 0.9)
    save('Toque 10.wav', render(ev, 5.6, epiano, tail=1.8))

    # 2 — "Faísca": corrida rápida e aguda, glockenspiel
    ev = motif_twice(['G5', 'C6', 'E6', 'D6', 'A5', 'C6'], 0.13, 0.55)
    save('Toque 11.wav', render(ev, 3.8, glock, tail=1.4))

    # 3 — "Subida": arpejo que sobe até o agudo, sino
    ev = motif_twice(['F5', 'A5', 'C6', 'F6', 'A6'], 0.17, 1.05, vol=0.7)
    save('Toque 12.wav', render(ev, 4.7, bell, tail=2.0))

    # 4 — "Cordas": motivo lento em Si menor, corda dedilhada com eco
    ev = motif_twice(['B4', 'D5', 'F#5', 'E5', 'B5'], 0.36, 1.7)
    save('Toque 1.wav', render(ev, 8.2, None, tail=2.2, echo=(0.27, 0.3)))

    # 5 — "Madeira": padrão rítmico de marimba
    pat = ['G4', 'D5', 'B4', 'D5', 'C5', 'D5', 'G4', 'B4']
    ev = motif_twice(pat, 0.16, 1.4, accent_last=False)
    save('Toque 2.wav', render(ev, 6.8, marimba, tail=1.0, echo=(0.16, 0.22)))

    # 6 — "Carrilhão": cascata descendo do agudo
    ev = motif_twice(['G6', 'E6', 'C6', 'G5', 'E5', 'C5'], 0.2, 1.5, vol=0.75)
    save('Toque 3.wav', render(ev, 6.4, chime, tail=2.2))

    # 7 — "Kalimba": melodia que desce devagar
    ev = motif_twice(['E6', 'D6', 'C6', 'A5', 'G5', 'E5'], 0.24, 1.15)
    save('Toque 4.wav', render(ev, 5.8, kalimba, tail=1.8))


if __name__ == '__main__':
    main()
