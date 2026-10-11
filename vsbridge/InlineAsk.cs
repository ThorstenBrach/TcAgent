// TcAgentBridge - Inline-Frage direkt im Code-Fenster (Strg+Umschalt+Q)
// Ein kleines Eingabefeld erscheint unter der Cursorzeile. Enter schickt die Frage an den TcAgent-Chat
// (der sie im Hintergrund beantwortet), Esc bricht ab; danach steht der Fokus wieder im Code.
//
// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace TcAgentBridge
{
    sealed class InlineAsk : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern bool GetCaretPos(out POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

        static InlineAsk open;

        readonly TextBox box;
        readonly string ctx;
        readonly System.Windows.Forms.Control editor;
        bool done;

        internal static void Open(string ctx, bool busy, string backendName)
        {
            open?.Finish(null);

            // Editor und Cursorposition merken, solange der Editor noch den Fokus hat
            var hwnd = GetFocus();
            System.Windows.Forms.Control editor = null;
            try { editor = System.Windows.Forms.Control.FromChildHandle(hwnd); } catch { }
            int lineH = editor?.Font?.Height ?? 16;
            POINT p = default;
            bool haveCaret = hwnd != IntPtr.Zero && GetCaretPos(out p) && ClientToScreen(hwnd, ref p);
            // Caret-Position nur glauben, wenn sie im Editor liegt (sonst z. B. 0,0 -> falscher Monitor)
            if (haveCaret && editor != null && !editor.RectangleToScreen(editor.ClientRectangle).Contains(p.X, p.Y)) haveCaret = false;
            if (!haveCaret)
            {
                if (editor != null) { var r = editor.RectangleToScreen(editor.ClientRectangle); p = new POINT { X = r.Left + 40, Y = r.Top + r.Height / 3 }; }
                else { var m = System.Windows.Forms.Cursor.Position; p = new POINT { X = m.X, Y = m.Y }; }
            }

            var w = new InlineAsk(ctx, editor, busy, backendName);
            open = w;
            w.PlaceAt(p.X, p.Y + lineH + 4);
            w.Show();
            w.Activate();
            w.box.Focus();
            Keyboard.Focus(w.box);
        }

        InlineAsk(string ctx, System.Windows.Forms.Control editor, bool busy, string backendName)
        {
            this.ctx = ctx;
            this.editor = editor;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = true;
            SizeToContent = SizeToContent.Height;
            Width = 560;
            try { Owner = Application.Current?.MainWindow; } catch { }

            box = new TextBox
            {
                AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, MinHeight = 24,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Padding = new Thickness(4, 2, 4, 2), IsEnabled = !busy
            };
            var where = "";
            if (!string.IsNullOrEmpty(ctx))
            {
                var lines = ctx.Split('\n');
                where = lines[0].Replace("Offenes Objekt: ", "");
                if (lines.Length > 1 && lines[1].StartsWith("Cursor:")) where += " · " + lines[1].Substring(8);
            }
            var hint = new TextBlock
            {
                Text = busy ? $"TcAgent arbeitet noch an der letzten Frage – Esc schliesst."
                            : $"Frage an {backendName}{(where.Length > 0 ? "  ·  " + where : "")}   –   Enter senden · Umschalt+Enter neue Zeile · Esc abbrechen",
                FontSize = 11, Opacity = 0.75, Margin = new Thickness(2, 0, 2, 4), TextTrimming = TextTrimming.CharacterEllipsis
            };
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(hint);
            panel.Children.Add(box);
            Content = new Border { BorderThickness = new Thickness(1), Child = panel };

            // VS-Farbschema
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ((Border)Content).SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.SetResourceReference(TextBox.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);

            box.PreviewKeyDown += OnKey;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
            Deactivated += (s, e) => Finish(null);   // Klick woanders hin = abbrechen
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(null); return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                var i = box.CaretIndex;
                box.Text = box.Text.Insert(i, Environment.NewLine);
                box.CaretIndex = i + Environment.NewLine.Length;
                return;
            }
            var text = box.Text.Trim();
            if (text.Length > 0) Finish(text);
        }

        void Finish(string text)
        {
            if (done) return;
            done = true;
            if (open == this) open = null;
            try { Close(); } catch { }
            if (text != null) BridgePackage.Instance?.SendInline(text, ctx);
            // Fokus zurueck in den Code (nach dem Schliessen, sonst holt ihn sich das Hauptfenster)
            var ed = editor;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { BridgePackage.Instance?.ReturnToEditor(); } catch { }
                try { if (ed != null && !ed.IsDisposed) ed.Focus(); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Pixel (Bildschirm) -> WPF-Einheiten, innerhalb des Arbeitsbereichs halten
        void PlaceAt(int px, int py)
        {
            // In Pixeln auf DEM Monitor begrenzen, auf dem der Cursor steht (SystemParameters.WorkArea ist nur der Hauptmonitor)
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(px, py)).WorkingArea;
            double sx = 1, sy = 1;
            try
            {
                var src = PresentationSource.FromVisual(Application.Current.MainWindow);
                if (src?.CompositionTarget != null) { var m = src.CompositionTarget.TransformFromDevice; sx = m.M11; sy = m.M22; }
            }
            catch { }
            int wPx = (int)(Width / sx), hPx = (int)(110 / sy);
            if (px + wPx > scr.Right) px = scr.Right - wPx - 8;
            if (py + hPx > scr.Bottom) py = scr.Bottom - hPx;
            px = Math.Max(scr.Left, px); py = Math.Max(scr.Top, py);
            Left = px * sx;
            Top = py * sy;
        }
    }
}
