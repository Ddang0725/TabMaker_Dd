using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TabMaker {
    public class TabView : FrameworkElement {
        public List<Note> Notes = new List<Note>();
        public int[] Tuning = Analysis.Tunings[0];
        public double Position, RangeStart, RangeEnd, SecondsVisible = 10;
        public BeatMap Rhythm = new BeatMap();
        public int BarsVisible = 2;
        public bool Follow = true;
        double viewStart;
        double VisibleUnits { get { return Rhythm.Available ? BarsVisible*Rhythm.Meter : SecondsVisible; } }
        double UnitAt(double time) { return Rhythm.Available ? Rhythm.BeatAt(time) : time; }
        double TimeAt(double unit) { return Rhythm.Available ? Rhythm.TimeAt(unit) : unit; }
        public event Action<double> Seek;
        static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(43,57,48));
        static readonly Brush Green = new SolidColorBrush(Color.FromRgb(46,104,73));
        public TabView() { MinHeight = 310; ClipToBounds = true; Cursor = Cursors.Hand; Focusable = true; }
        void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) {
            dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x,y));
        }
        public static int ActiveIndex(List<Note> notes, double time) {
            // Binary search also handles pauses and reverse seeks without stale state.
            int left = 0, right = notes.Count - 1, found = -1;
            while (left <= right) { int mid = (left + right) / 2; if (notes[mid].Start <= time) { found = mid; left = mid + 1; } else right = mid - 1; }
            return found >= 0 && time < notes[found].End ? found : -1;
        }
        public void Update(double position) {
            Position = position;
            double first=UnitAt(RangeStart),last=UnitAt(RangeEnd);
            if (Follow) viewStart = Math.Max(first, Math.Min(Math.Max(first,last-VisibleUnits), UnitAt(position)-VisibleUnits*.24));
            InvalidateVisual();
        }
        public void ResetView() { viewStart = UnitAt(RangeStart); Update(Position); }
        public static List<int> ActiveIndices(List<Note> notes,double time) {
            List<int> active=new List<int>();
            for(int i=0;i<notes.Count && notes[i].Start<=time;i++) if(time<notes[i].End) active.Add(i);
            return active;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e) {
            base.OnMouseDown(e);
            double width = Math.Max(1, ActualWidth - 82);
            double time = TimeAt(viewStart + (e.GetPosition(this).X - 58) / width * VisibleUnits);
            if (Seek != null) Seek(Math.Max(RangeStart, Math.Min(RangeEnd, time)));
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e) {
            Follow = false; double first=UnitAt(RangeStart),last=UnitAt(RangeEnd);
            viewStart = Math.Max(first, Math.Min(Math.Max(first,last-VisibleUnits), viewStart-e.Delta/120.0*(Rhythm.Available ? Rhythm.Meter : 1)));
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnRender(DrawingContext dc) {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight, left = 58, right = w - 24, width = Math.Max(1, right - left);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0,0,w,h), 12,12);
            if (Notes.Count == 0 && !Rhythm.Available) {
                Text(dc,"음원을 선택하고 타브를 만들어주세요.",30,95,19,Ink);
                Text(dc,"재생하면 현재 위치와 프렛이 노래에 맞춰 움직입니다.",30,135,13,Brushes.Gray); return;
            }
            double top = 75, spacing = Math.Max(30, Math.Min(49, (h - 145) / 5));
            Pen grid = new Pen(new SolidColorBrush(Color.FromRgb(230,234,227)),1);
            if(Rhythm.Available) {
                double bar=Math.Floor(Rhythm.BeatAt(Position)/Rhythm.Meter)*Rhythm.Meter;
                double x1=Math.Max(left,left+(bar-viewStart)/VisibleUnits*width);
                double x2=Math.Min(right,left+(bar+Rhythm.Meter-viewStart)/VisibleUnits*width);
                if(x2>x1) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(242,248,236)),null,new Rect(x1,47,x2-x1,top+spacing*5-24));
            }
            double tick = Rhythm.Available ? 1 : SecondsVisible <= 8 ? .5 : SecondsVisible <= 16 ? 1 : 2;
            for (double t = Math.Ceiling(viewStart / tick) * tick; t <= viewStart + VisibleUnits; t += tick) {
                double x = left + (t - viewStart) / VisibleUnits * width;
                bool bar=Rhythm.Available && Math.Abs(t/Rhythm.Meter-Math.Round(t/Rhythm.Meter))<.001;
                dc.DrawLine(bar ? new Pen(new SolidColorBrush(Color.FromRgb(103,136,91)),2) : grid, new Point(x,48), new Point(x,top + spacing*5 + 16));
                if(Rhythm.Available) {
                    int number=(int)Math.Floor(t/Rhythm.Meter)+1;
                    Text(dc,bar ? (number>0 ? number+"마디" : "앞부분") : (((int)t%Rhythm.Meter+Rhythm.Meter)%Rhythm.Meter+1)+"박",x+3,bar ? 15 : 30,bar ? 11 : 9,bar ? Green : Brushes.Gray);
                } else Text(dc,t.ToString("0.0",CultureInfo.InvariantCulture)+"s",x-8,22,10,Brushes.Gray);
            }
            string[] pitchNames = {"C","C#","D","D#","E","F","F#","G","G#","A","A#","B"};
            for (int s=0;s<6;s++) {
                double y = top + s * spacing;
                Text(dc,(s+1)+" "+pitchNames[Tuning[s]%12],11,y-10,12,Green);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(180,193,175)), 1+s*.18),new Point(left,y),new Point(right,y));
            }
            List<int> active = ActiveIndices(Notes,Position);
            dc.PushClip(new RectangleGeometry(new Rect(left-1,47,width+2,h-47)));
            for (int i=0;i<Notes.Count;i++) {
                Note n = Notes[i];
                if(UnitAt(n.End) < viewStart || UnitAt(n.Start) > viewStart + VisibleUnits) continue;
                double x = left + (UnitAt(n.Start)-viewStart)/VisibleUnits*width;
                double y = n.String < 0 ? top + spacing*5 + 30 : top + n.String*spacing;
                double durationWidth = Math.Max(3,(UnitAt(n.End)-UnitAt(n.Start))/VisibleUnits*width);
                bool current = active.Contains(i);
                dc.DrawRoundedRectangle(new SolidColorBrush(current ? Color.FromRgb(91,162,105) : Color.FromRgb(207,225,198)),null,new Rect(x,y-4,durationWidth,8),4,4);
                dc.DrawRoundedRectangle(current ? Green : Brushes.White,new Pen(current ? Green : new SolidColorBrush(Color.FromRgb(164,186,153)),1),new Rect(x-11,y-13,25,26),6,6);
                Text(dc,n.Fret < 0 ? "?" : n.Fret.ToString(), x-6,y-10,14,current ? Brushes.White : Ink);
            }
            if(UnitAt(Position) >= viewStart && UnitAt(Position) <= viewStart + VisibleUnits) {
                double x = left+(UnitAt(Position)-viewStart)/VisibleUnits*width;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(224,121,55)),2),new Point(x,47),new Point(x,top+spacing*5+18));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(224,121,55)),null,new Point(x,49),4,4);
            }
            dc.Pop();
            Text(dc,Follow ? "재생 위치 자동 따라가기 · 타브 클릭으로 이동" : "화면 고정 · 마우스 휠로 구간 이동 · 따라가기를 켜면 복귀",15,h-25,11,Brushes.Gray);
        }
    }
}
