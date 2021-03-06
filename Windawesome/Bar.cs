﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Windawesome
{
	public sealed class Bar : IBar
	{
		private readonly Monitor monitor;

		private readonly int barHeight;
		private readonly NonActivatableForm form;
		private readonly IFixedWidthWidget[] leftAlignedWidgets;
		private readonly IFixedWidthWidget[] rightAlignedWidgets;
		private readonly ISpanWidget[] middleAlignedWidgets;
		private readonly Font font;

		private int rightmostLeftAlign;
		private int leftmostRightAlign;

		private static readonly HashSet<Type> widgetTypes = new HashSet<Type>();

		public class NonActivatableForm : Form
		{
			protected override CreateParams CreateParams
			{
				get
				{
					var createParams = base.CreateParams;
					// make the form not activatable
					createParams.ExStyle |= (int)
						(NativeMethods.WS_EX.WS_EX_NOACTIVATE | NativeMethods.WS_EX.WS_EX_TOOLWINDOW | NativeMethods.WS_EX.WS_EX_TOPMOST);
					return createParams;
				}
			}

			protected override bool ShowWithoutActivation { get { return true; } }

			protected override void WndProc(ref Message m)
			{
				if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
				{
					m.Result = NativeMethods.MA_NOACTIVATE;
				}
				else if (m.Msg == NativeMethods.WM_SYSCOMMAND &&
					(m.WParam == NativeMethods.SC_MINIMIZESigned || m.WParam == NativeMethods.SC_MAXIMIZESigned))
				{
					m.Result = IntPtr.Zero;
				}
				else
				{
					base.WndProc(ref m);
				}
			}
		}

		#region Events

		private delegate void SpanWidgetControlsAddedEventHandler(ISpanWidget widget, IEnumerable<Control> controls);
		private event SpanWidgetControlsAddedEventHandler SpanWidgetControlsAdded;

		private delegate void SpanWidgetControlsRemovedEventHandler(ISpanWidget widget, IEnumerable<Control> controls);
		private event SpanWidgetControlsRemovedEventHandler SpanWidgetControlsRemoved;

		private delegate void FixedWidthWidgetWidthChangedEventHandler(IFixedWidthWidget widget);
		private event FixedWidthWidgetWidthChangedEventHandler FixedWidthWidgetWidthChanged;

		private delegate void WidgetControlsChangedEventHandler(IWidget widget, IEnumerable<Control> oldControls, IEnumerable<Control> newControls);
		private event WidgetControlsChangedEventHandler WidgetControlsChanged;

		public delegate void BarShownEventHandler();
		public event BarShownEventHandler BarShown;

		public delegate void BarHiddenEventHandler();
		public event BarHiddenEventHandler BarHidden;

		public void DoWidgetControlsChanged(IWidget widget, IEnumerable<Control> controlsRemoved, IEnumerable<Control> controlsAdded)
		{
			WidgetControlsChanged(widget, controlsRemoved, controlsAdded);
		}

		public void DoSpanWidgetControlsAdded(ISpanWidget widget, IEnumerable<Control> controls)
		{
			SpanWidgetControlsAdded(widget, controls);
		}

		public void DoSpanWidgetControlsRemoved(ISpanWidget widget, IEnumerable<Control> controls)
		{
			SpanWidgetControlsRemoved(widget, controls);
		}

		public void DoFixedWidthWidgetWidthChanged(IFixedWidthWidget widget)
		{
			FixedWidthWidgetWidthChanged(widget);
		}

		private void DoBarShown()
		{
			if (BarShown != null)
			{
				BarShown();
			}
		}

		private void DoBarHidden()
		{
			if (BarHidden != null)
			{
				BarHidden();
			}
		}

		#endregion

		public Bar(Monitor monitor, IEnumerable<IFixedWidthWidget> leftAlignedWidgets, IEnumerable<IFixedWidthWidget> rightAlignedWidgets,
			IEnumerable<ISpanWidget> middleAlignedWidgets, int barHeight = 20, Font font = null, Color? backgroundColor = null)
		{
			this.monitor = monitor;
			this.leftAlignedWidgets = leftAlignedWidgets.ToArray();
			this.rightAlignedWidgets = rightAlignedWidgets.ToArray();
			this.middleAlignedWidgets = middleAlignedWidgets.ToArray();
			this.barHeight = barHeight;
			this.font = font ?? new Font("Lucida Console", 8);

			this.form = CreateForm();
			if (backgroundColor != null)
			{
				this.form.BackColor = backgroundColor.Value;
			}
		}

		public override int GetHashCode()
		{
			return this.form.Handle.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			var bar = obj as Bar;
			return bar != null && this.form.Handle == bar.form.Handle;
		}

		#region IBar Members

		void IBar.InitializeBar(Windawesome windawesome)
		{
			// statically initialize all widgets
			// this statement uses the laziness of Where
			this.leftAlignedWidgets.Cast<IWidget>().Concat(this.rightAlignedWidgets).Concat(this.middleAlignedWidgets).
				Where(w => !widgetTypes.Contains(w.GetType())).
				ForEach(w => { w.StaticInitializeWidget(windawesome); widgetTypes.Add(w.GetType()); });

			WidgetControlsChanged = OnWidgetControlsChanged;
			SpanWidgetControlsAdded = OnSpanWidgetControlsAdded;
			SpanWidgetControlsRemoved = OnSpanWidgetControlsRemoved;
			FixedWidthWidgetWidthChanged = OnFixedWidthWidgetWidthChanged;

			leftAlignedWidgets.ForEach(w => w.InitializeWidget(this));
			rightAlignedWidgets.ForEach(w => w.InitializeWidget(this));
			middleAlignedWidgets.ForEach(w => w.InitializeWidget(this));

			// get initial controls
			this.form.SuspendLayout();

			this.leftAlignedWidgets.SelectMany(widget => widget.GetInitialControls(true)).ForEach(this.form.Controls.Add);
			this.rightAlignedWidgets.SelectMany(widget => widget.GetInitialControls(false)).ForEach(this.form.Controls.Add);
			this.middleAlignedWidgets.SelectMany(widget => widget.GetInitialControls()).ForEach(this.form.Controls.Add);

			this.form.ResumeLayout();
		}

		void IBar.Dispose()
		{
			leftAlignedWidgets.ForEach(w => w.Dispose());
			rightAlignedWidgets.ForEach(w => w.Dispose());
			middleAlignedWidgets.ForEach(w => w.Dispose());

			// statically dispose of all widgets
			// this statement uses the laziness of Where
			this.leftAlignedWidgets.Cast<IWidget>().Concat(this.rightAlignedWidgets).Concat(this.middleAlignedWidgets).
				Where(w => widgetTypes.Contains(w.GetType())).
				ForEach(w => { w.StaticDispose(); widgetTypes.Remove(w.GetType()); });

			this.form.Dispose();
		}

		public int GetBarHeight()
		{
			return barHeight;
		}

		IntPtr IBar.Handle { get { return this.form.Handle; } }

		public Monitor Monitor { get { return this.monitor; } }

		void IBar.OnClientWidthChanging(int newWidth)
		{
			if (this.form.ClientSize.Width != newWidth)
			{
				ResizeWidgets(newWidth);
			}
		}

		void IBar.Show()
		{
			if (!this.form.Visible)
			{
				this.form.Show();
				DoBarShown();
			}
		}

		void IBar.Hide()
		{
			this.form.Hide();
			DoBarHidden();
		}

		void IBar.Refresh()
		{
			this.leftAlignedWidgets.Cast<IWidget>().Concat(this.rightAlignedWidgets).Concat(this.middleAlignedWidgets).
				ForEach(w => w.Refresh());
		}

		#endregion

		#region Event Handlers

		private void OnWidgetControlsChanged(IWidget widget, IEnumerable<Control> controlsRemoved, IEnumerable<Control> controlsAdded)
		{
			this.form.SuspendLayout();

			controlsRemoved.ForEach(this.form.Controls.Remove);
			controlsAdded.ForEach(this.form.Controls.Add);

			if (widget is IFixedWidthWidget)
			{
				ResizeWidgets(widget as IFixedWidthWidget);
			}

			this.form.ResumeLayout();
		}

		private void OnSpanWidgetControlsAdded(ISpanWidget widget, IEnumerable<Control> controls)
		{
			this.form.SuspendLayout();

			controls.ForEach(this.form.Controls.Add);

			this.form.ResumeLayout();
		}

		private void OnSpanWidgetControlsRemoved(ISpanWidget widget, IEnumerable<Control> controls)
		{
			this.form.SuspendLayout();

			controls.ForEach(this.form.Controls.Remove);

			this.form.ResumeLayout();
		}

		private void OnFixedWidthWidgetWidthChanged(IFixedWidthWidget widget)
		{
			this.form.SuspendLayout();

			ResizeWidgets(widget);

			this.form.ResumeLayout();
		}

		#endregion

		private static NonActivatableForm CreateForm()
		{
			var newForm = new NonActivatableForm
				{
					StartPosition = FormStartPosition.Manual,
					FormBorderStyle = FormBorderStyle.FixedToolWindow,
					AutoValidate = AutoValidate.Disable,
					CausesValidation = false,
					ControlBox = false,
					MaximizeBox = false,
					MinimizeBox = false,
					ShowIcon = false,
					ShowInTaskbar = false,
					SizeGripStyle = SizeGripStyle.Hide,
					AutoScaleMode = AutoScaleMode.Font,
					AutoScroll = false,
					AutoSize = false,
					HelpButton = false,
					TopLevel = true,
					WindowState = FormWindowState.Normal,
					ClientSize = new Size(0, 0)
				};

			return newForm;
		}

		private void ResizeWidgets(int newWidth)
		{
			RepositionLeftAlignedWidgets(0, 0);
			RepositionRightAlignedWidgets(rightAlignedWidgets.Length - 1, newWidth);
			RepositionMiddleAlignedWidgets();
		}

		private void ResizeWidgets(IFixedWidthWidget widget)
		{
			int index;
			if ((index = Array.IndexOf(leftAlignedWidgets, widget)) != -1)
			{
				RepositionLeftAlignedWidgets(index + 1, widget.GetRight());
			}
			else
			{
				RepositionRightAlignedWidgets(Array.IndexOf(rightAlignedWidgets, widget) - 1, widget.GetLeft());
			}

			RepositionMiddleAlignedWidgets();
		}

		private void RepositionLeftAlignedWidgets(int fromIndex, int fromX)
		{
			for (var i = fromIndex; i < leftAlignedWidgets.Length; i++)
			{
				var w = leftAlignedWidgets[i];
				w.RepositionControls(fromX, -1);
				fromX = w.GetRight();
			}

			rightmostLeftAlign = fromX;
		}

		private void RepositionRightAlignedWidgets(int fromIndex, int toX)
		{
			for (var i = fromIndex; i >= 0; i--)
			{
				var w = rightAlignedWidgets[i];
				w.RepositionControls(-1, toX);
				toX = w.GetLeft();
			}

			leftmostRightAlign = toX;
		}

		private void RepositionMiddleAlignedWidgets()
		{
			if (middleAlignedWidgets.Length > 0)
			{
				var eachWidth = (leftmostRightAlign - rightmostLeftAlign) / middleAlignedWidgets.Length;
				var x = rightmostLeftAlign;
				foreach (var w in middleAlignedWidgets)
				{
					w.RepositionControls(x, x + eachWidth);
					x += eachWidth;
				}
			}
		}

		public Label CreateLabel(string text, int xLocation, int width = -1)
		{
			var label = new Label();
			label.SuspendLayout();
			label.AutoSize = false;
			label.AutoEllipsis = true;
			label.Text = text;
			label.Font = font;
			label.Size = new Size(width == -1 ? TextRenderer.MeasureText(label.Text, label.Font).Width : width, this.barHeight);
			label.Location = new Point(xLocation, 0);
			label.TextAlign = ContentAlignment.MiddleLeft; // TODO: this doesn't work when there are ellipsis for certain fonts/font-sizes
			label.ResumeLayout();

			return label;
		}
	}
}
