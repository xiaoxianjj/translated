using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;

namespace Translated
{
    internal enum MotionState { Translating, Replaced, Copied, Review, Failed }

    internal sealed class Motion : OverlayWindow
    {
        readonly Badge badge = new Badge();
        readonly DispatcherTimer dismiss = new DispatcherTimer();
        readonly Stopwatch clock = new Stopwatch();
        bool rendering;
        internal Motion() : base(false)
        {
            Content = badge;
            AutomationProperties.SetLiveSetting(badge, AutomationLiveSetting.Polite);
            dismiss.Tick += (s,e) => Dismiss();
        }

        internal void Display(MotionState state, DrawingPoint point, string reason = null)
        {
            dismiss.Stop();
            bool transition = IsVisible && badge.State == MotionState.Translating && state != MotionState.Translating;
            badge.State = state; badge.Reason = reason ?? "";
            badge.Animated = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            badge.Transition = transition && badge.Animated;
            clock.Restart(); badge.Elapsed = 0;
            string name = state == MotionState.Failed ? "Translation failed. " + reason : Label(state);
            Title = name; AutomationProperties.SetName(badge, name);
            bool failure = state == MotionState.Failed;
            ShowAt(point, failure ? 310 : 176, failure ? 57 : 40);
            badge.InvalidateVisual();
            SetRendering(badge.Animated && (badge.Transition || state == MotionState.Translating));
            if (state != MotionState.Translating)
            {
                dismiss.Interval = TimeSpan.FromMilliseconds(2000 + (badge.Transition ? 360 : 0));
                dismiss.Start();
            }
        }
        static string Label(MotionState state) => state == MotionState.Review ? "Review result" : state.ToString();
        void Frame(object sender, EventArgs e)
        {
            badge.Elapsed = clock.Elapsed.TotalSeconds;
            if (!SystemParameters.ClientAreaAnimation) badge.Animated = badge.Transition = false;
            badge.InvalidateVisual();
            if (!badge.Animated || (badge.State != MotionState.Translating && badge.Elapsed >= .36)) SetRendering(false);
        }
        void SetRendering(bool value)
        {
            if (value == rendering) return;
            rendering = value;
            if (value) CompositionTarget.Rendering += Frame;
            else CompositionTarget.Rendering -= Frame;
        }
        internal void Dismiss() { dismiss.Stop(); SetRendering(false); if (IsVisible) Hide(); }
        public override void Dispose() { Dismiss(); base.Dispose(); }

        internal sealed class Badge : FrameworkElement
        {
            internal MotionState State;
            internal string Reason = "";
            internal bool Animated, Transition;
            internal double Elapsed;
            static readonly Color Violet = Color.FromRgb(189,174,250), Pink = Color.FromRgb(246,194,216),
                Mint = Color.FromRgb(149,228,210), Lime = Color.FromRgb(217,245,175),
                Plum = Color.FromRgb(48,29,77), Pine = Color.FromRgb(49,76,65);
            static readonly Typeface Face = new Typeface((FontFamily)Application.Current.FindResource("RoundFont"),
                FontStyles.Normal,FontWeights.Bold,FontStretches.Normal);
            static readonly Typeface Detail = new Typeface("Segoe UI, Microsoft YaHei UI");
            static Color Blend(Color a, Color b, double t) => Color.FromRgb(
                (byte)Math.Round(a.R+(b.R-a.R)*t),(byte)Math.Round(a.G+(b.G-a.G)*t),(byte)Math.Round(a.B+(b.B-a.B)*t));
            static Pen Stroke(Brush brush,double size) => new Pen(brush,size) { StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            FormattedText Type(string s, double size, Brush brush, bool detail=false) => new FormattedText(s,
                CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,detail ? Detail : Face,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);

            protected override void OnRender(DrawingContext dc)
            {
                bool busy = State == MotionState.Translating, failed = State == MotionState.Failed;
                double t = busy ? 0 : Transition ? Math.Min(1,Elapsed/.36) : 1;
                double mix = t*t*(3-2*t);
                bool attention = failed || State == MotionState.Review;
                Color left = Blend(Violet,attention ? Color.FromRgb(251,174,154) : Mint,mix);
                Color right = Blend(Pink,attention ? Color.FromRgb(255,215,174) : Lime,mix);
                bool high = SystemParameters.HighContrast;
                Brush ink = high ? SystemColors.WindowTextBrush : new SolidColorBrush(Blend(Plum,failed ? Color.FromRgb(95,53,44) : Pine,mix));
                Brush fill = high ? SystemColors.WindowBrush : new LinearGradientBrush(left,right,0);
                dc.DrawRoundedRectangle(fill, new Pen(high ? ink : new SolidColorBrush(Color.FromArgb(145,255,255,255)),1),
                    new Rect(.5,.5,ActualWidth-1,ActualHeight-1),ActualHeight/2,ActualHeight/2);
                if (failed)
                {
                    var headline = Type("Translation failed",15,ink);
                    var detail = Type(Reason,12,ink,true);
                    detail.MaxTextWidth = Math.Max(40,ActualWidth-61); detail.Trimming = TextTrimming.CharacterEllipsis; detail.MaxLineCount = 1;
                    dc.DrawEllipse(high ? SystemColors.ControlBrush : Brushes.Bisque,null,new Point(24,ActualHeight/2),12,12);
                    dc.DrawText(Type("!",17,ink),new Point(21,ActualHeight/2-12));
                    dc.DrawText(headline,new Point(46,6)); dc.DrawText(detail,new Point(46,29)); return;
                }
                var center = new Point(45,20);
                dc.DrawEllipse(high ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb(125,255,255,255)),null,center,12,12);
                if (mix < 1)
                {
                    dc.PushOpacity(1-mix);
                    dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromArgb(47,76,51,111)),2),center,7,7);
                    double angle = (Animated ? Elapsed * 430 : 0) - 77;
                    Point At(double a) => new Point(center.X + 7*Math.Cos(a*Math.PI/180),center.Y + 7*Math.Sin(a*Math.PI/180));
                    var arc = new StreamGeometry();
                    using (var c = arc.Open()) { c.BeginFigure(At(angle),false,false); c.ArcTo(At(angle+246),new Size(7,7),0,true,SweepDirection.Clockwise,true,false); }
                    dc.DrawGeometry(null,Stroke(ink,2.1),arc); dc.Pop();
                }
                if (mix > 0 && State == MotionState.Replaced)
                {
                    var a = new Point(38.5,20.2); var b = new Point(42.7,24.1); var c = new Point(51.5,15.7);
                    Point Between(Point u,Point v,double f) => new Point(u.X+(v.X-u.X)*f,u.Y+(v.Y-u.Y)*f);
                    var pen = Stroke(ink,2.5);
                    dc.PushOpacity(mix);
                    dc.DrawLine(pen,a,Between(a,b,Math.Min(1,mix/.32)));
                    if (mix > .32) dc.DrawLine(pen,b,Between(b,c,(mix-.32)/.68));
                    dc.Pop();
                }
                const double x = 65;
                var progress = Type("Translating",15.5,ink);
                double y = (40-progress.Height)/2;
                if (mix < 1)
                {
                    dc.PushOpacity(1-mix); dc.DrawText(progress,new Point(x,y-6*mix)); dc.Pop();
                }
                if (mix > 0)
                {
                    dc.PushOpacity(mix); dc.DrawText(Type(Label(State),15.5,ink),new Point(x,y+6*(1-mix))); dc.Pop();
                }
                if (State == MotionState.Copied)
                {
                    dc.PushOpacity(mix);
                    var pen = Stroke(ink,1.35);
                    dc.DrawRoundedRectangle(null,pen,new Rect(40,15,10,12),1.5,1.5);
                    dc.DrawRoundedRectangle(fill,pen,new Rect(42,13,6,3),.8,.8);
                    dc.Pop();
                }
                if (State == MotionState.Review)
                {
                    dc.PushOpacity(mix); dc.DrawText(Type("!",16,ink),new Point(42,9)); dc.Pop();
                }
            }
        }
    }
}
