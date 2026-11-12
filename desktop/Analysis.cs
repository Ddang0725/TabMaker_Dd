using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TabMaker {
    public class Note {
        public double Start, Duration;
        public double Confidence = 1;
        public int Midi, String = -1, Fret = -1;
        public double End { get { return Start + Duration; } }
        public string Name { get { return new[]{"C","C#","D","D#","E","F","F#","G","G#","A","A#","B"}[Midi % 12] + (Midi / 12 - 1); } }
    }
    public static class Analysis {
        public static readonly int[][] Tunings = {new[]{64,59,55,50,45,40},new[]{64,59,55,50,45,38},new[]{63,58,54,49,44,39}};
        public static int Pitch(float[] samples, int offset) {
            const int size = 768, maximum = 110;
            double energy = 0;
            for (int j = 0; j < size; j++) energy += samples[offset + j] * samples[offset + j];
            if (Math.Sqrt(energy / size) < 0.009) return -1;
            double[] difference = new double[maximum + 1]; double sum = 0;
            for (int lag = 1; lag <= maximum; lag++) {
                double d = 0;
                for (int j = 0; j < size; j++) { double delta = samples[offset + j] - samples[offset + j + lag]; d += delta * delta; }
                sum += d; difference[lag] = sum > 0 ? d * lag / sum : 1;
            }
            for (int lag = 5; lag < maximum; lag++) {
                if (difference[lag] >= 0.16) continue;
                while (lag + 1 < maximum && difference[lag + 1] < difference[lag]) lag++;
                double left = difference[lag - 1], mid = difference[lag], right = difference[lag + 1];
                double denominator = left - 2 * mid + right;
                double refined = lag + (Math.Abs(denominator) > 1e-12 ? Math.Max(-0.5, Math.Min(0.5, (left - right) / (2 * denominator))) : 0);
                int midi = (int)Math.Round(69 + 12 * Math.Log(8000 / refined / 440, 2));
                return midi >= 36 && midi <= 88 ? midi : -1;
            }
            return -1;
        }
        public static List<Note> Run(float[] samples, double start, double duration, int[] tuning, int maxFret, CancellationToken token, Action<int> progress) {
            int first = (int)(start * 8000), last = Math.Min(samples.Length, (int)((start + duration) * 8000));
            List<int> frames = new List<int>();
            for (int i = first; i + 878 <= last; i += 160) {
                token.ThrowIfCancellationRequested(); frames.Add(Pitch(samples, i));
                if (frames.Count % 25 == 0) progress(25 + (int)(65.0 * (i - first) / Math.Max(1, last - first)));
            }
            int[] smoothed = frames.ToArray();
            for (int i = 0; i < frames.Count; i++) {
                if (frames[i] < 0) continue;
                List<int> adjacent = new List<int>();
                for (int k = Math.Max(0, i - 1); k <= Math.Min(frames.Count - 1, i + 1); k++) if (frames[k] >= 0) adjacent.Add(frames[k]);
                adjacent.Sort(); smoothed[i] = adjacent[adjacent.Count / 2];
            }
            List<Note> notes = new List<Note>();
            for (int i = 0; i < smoothed.Length;) {
                int end = i + 1;
                while (end < smoothed.Length && smoothed[end] == smoothed[i]) end++;
                if (smoothed[i] >= 0 && (end - i) >= 4) notes.Add(new Note {Midi = smoothed[i], Start = start + i * .02 + .048, Duration = (end - i) * .02});
                i = end;
            }
            Finger(notes, tuning, maxFret); progress(100); return notes;
        }
        class Choice { public int String, Fret, Previous; public double Cost; }
        class Assignment { public int Index,String,Fret; public Assignment Previous; }
        class Hand {
            public double[] Ends=new double[6];
            public int[] Frets=new int[6];
            public double Cost, Position=3;
            public Assignment Last;
        }
        public static void FingerPolyphonic(List<Note> notes,int[] tuning,int maxFret) {
            List<Hand> beam=new List<Hand> {new Hand()};
            for(int i=0;i<notes.Count;i++) {
                Note note=notes[i]; List<Hand> candidates=new List<Hand>();
                foreach(Hand hand in beam) {
                    for(int s=0;s<6;s++) {
                        int fret=note.Midi-tuning[s];
                        if(fret<0 || fret>maxFret || hand.Ends[s]>note.Start+.025) continue;
                        List<int> active=new List<int>();
                        for(int c=0;c<6;c++) if(hand.Ends[c]>note.Start+.025 && hand.Frets[c]>0) active.Add(hand.Frets[c]);
                        if(fret>0) active.Add(fret);
                        if(active.Count>1 && active.Max()-active.Min()>5) continue;
                        Hand next=new Hand {Ends=(double[])hand.Ends.Clone(),Frets=(int[])hand.Frets.Clone(),Cost=hand.Cost,Position=hand.Position};
                        next.Cost+=fret*.055+(fret>0 ? Math.Abs(fret-hand.Position)*.25 : -.1);
                        next.Ends[s]=note.End; next.Frets[s]=fret;
                        if(fret>0) next.Position=fret;
                        next.Last=new Assignment {Index=i,String=s,Fret=fret,Previous=hand.Last}; candidates.Add(next);
                    }
                    // Keep impossible or overlapping hypotheses explicit, never overwrite another string.
                    candidates.Add(new Hand {Ends=(double[])hand.Ends.Clone(),Frets=(int[])hand.Frets.Clone(),Position=hand.Position,Cost=hand.Cost+8+note.Confidence*12,Last=new Assignment {Index=i,String=-1,Fret=-1,Previous=hand.Last}});
                }
                beam=candidates.OrderBy(h=>h.Cost).Take(32).ToList();
            }
            if(beam.Count==0) return;
            for(Assignment at=beam[0].Last;at!=null;at=at.Previous) { notes[at.Index].String=at.String; notes[at.Index].Fret=at.Fret; }
        }
        public static void Finger(List<Note> notes, int[] tuning, int maxFret) {
            List<List<Choice>> layers = new List<List<Choice>>();
            foreach (Note note in notes) {
                List<Choice> choices = new List<Choice>();
                for (int s = 0; s < 6; s++) {
                    int fret = note.Midi - tuning[s];
                    if (fret < 0 || fret > maxFret) continue;
                    Choice choice = new Choice {String = s, Fret = fret, Cost = double.PositiveInfinity};
                    double own = fret * .07 - (fret == 0 ? .2 : 0);
                    if (layers.Count == 0) choice.Cost = own;
                    else {
                        List<Choice> previous = layers[layers.Count - 1];
                        for (int p = 0; p < previous.Count; p++) {
                            Choice before = previous[p];
                            double cost = before.Cost + own + (before.Fret < 0 ? 0 : Math.Abs(fret - before.Fret) * .65 + Math.Abs(s - before.String) * .9);
                            if (cost < choice.Cost) { choice.Cost = cost; choice.Previous = p; }
                        }
                    }
                    choices.Add(choice);
                }
                if (choices.Count == 0) {
                    List<Choice> prev = layers.Count > 0 ? layers[layers.Count - 1] : null;
                    int best = prev == null ? 0 : Enumerable.Range(0, prev.Count).OrderBy(i => prev[i].Cost).First();
                    choices.Add(new Choice {String=-1, Fret=-1, Previous=best, Cost=prev == null ? 0 : prev[best].Cost});
                }
                layers.Add(choices);
            }
            if (layers.Count == 0) return;
            List<Choice> final = layers[layers.Count - 1];
            int index = Enumerable.Range(0, final.Count).OrderBy(i => final[i].Cost).First();
            for (int i = notes.Count - 1; i >= 0; i--) { Choice c = layers[i][index]; notes[i].String = c.String; notes[i].Fret = c.Fret; index = c.Previous; }
        }
    }
}
