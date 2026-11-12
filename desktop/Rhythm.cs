using System;
using System.Collections.Generic;
using System.Linq;

namespace TabMaker {
    public sealed class BeatMap {
        public double Bpm, Confidence, Origin;
        public int Meter = 4;
        public List<double> Beats = new List<double>();
        public bool ManualTempo;
        public bool Available { get { return Bpm>=30 && Bpm<=300; } }
        double Raw(double time) {
            if(ManualTempo || Beats.Count<2) return time*Bpm/60;
            int left=0,right=Beats.Count-1,index=-1;
            while(left<=right) { int mid=(left+right)/2; if(Beats[mid]<=time) { index=mid;left=mid+1; } else right=mid-1; }
            if(index<0) return (time-Beats[0])/(Beats[1]-Beats[0]);
            if(index>=Beats.Count-1) return index+(time-Beats[index])/(Beats[index]-Beats[index-1]);
            return index+(time-Beats[index])/(Beats[index+1]-Beats[index]);
        }
        double TimeAtRaw(double beat) {
            if(ManualTempo || Beats.Count<2) return beat*60/Bpm;
            int index=(int)Math.Floor(beat);
            if(index<0) return Beats[0]+beat*(Beats[1]-Beats[0]);
            if(index>=Beats.Count-1) return Beats[Beats.Count-1]+(beat-(Beats.Count-1))*(Beats[Beats.Count-1]-Beats[Beats.Count-2]);
            return Beats[index]+(beat-index)*(Beats[index+1]-Beats[index]);
        }
        public double BeatAt(double time) { return Available ? Raw(time)-Raw(Origin) : time; }
        public double TimeAt(double beat) { return Available ? TimeAtRaw(beat+Raw(Origin)) : beat; }
        public int BarAt(double time) { return (int)Math.Floor(BeatAt(time)/Meter)+1; }
        public int BeatInBar(double time) { double beat=BeatAt(time); return (int)Math.Floor(beat-Math.Floor(beat/Meter)*Meter)+1; }
    }
}
