using System;
using System.Drawing;
using System.Windows.Forms;

namespace SqlResultExporter;

internal sealed class AppButton : Button
{
    private readonly bool primary;

    internal AppButton(bool primary = false)
    {
        this.primary = primary;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        ApplyPalette();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        bool highlighted = Enabled && primary;
        BackColor = !Enabled ? Color.FromArgb(237, 241, 245)
            : highlighted ? Color.FromArgb(34, 99, 184) : Color.White;
        ForeColor = !Enabled ? Color.FromArgb(83, 99, 116)
            : highlighted ? Color.White : Color.FromArgb(30, 53, 76);
        FlatAppearance.BorderColor = highlighted ? BackColor : Color.FromArgb(203, 214, 226);
        FlatAppearance.MouseOverBackColor = !Enabled ? BackColor
            : highlighted ? Color.FromArgb(29, 82, 153) : Color.FromArgb(233, 241, 249);
        FlatAppearance.MouseDownBackColor = !Enabled ? BackColor
            : highlighted ? Color.FromArgb(26, 73, 137) : Color.FromArgb(223, 235, 247);
    }
}
