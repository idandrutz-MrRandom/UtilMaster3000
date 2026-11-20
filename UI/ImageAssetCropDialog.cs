using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

internal sealed class ImageAssetCropDialog : Form
{
    private readonly Image image;
    private readonly Size targetSize;
    private readonly DoublePanel preview = new();
    private readonly ComboBox fit = new();
    private readonly TrackBar zoom = new();
    private readonly Label zoomLabel = new();
    private SkinImageAsset asset;
    private Point dragPoint;
    private bool dragging;

    public SkinImageAsset Result => new()
    {
        Path = asset.Path,
        Fit = asset.Fit,
        Zoom = asset.Zoom,
        CropX = asset.CropX,
        CropY = asset.CropY
    };

    public ImageAssetCropDialog(
        string title,
        Image image,
        Size targetSize,
        SkinImageAsset initial)
    {
        this.image = image;
        this.targetSize = targetSize;
        asset = new SkinImageAsset
        {
            Path = initial.Path,
            Fit = initial.Fit,
            Zoom = Math.Clamp(initial.Zoom, 1f, 4f),
            CropX = Math.Clamp(initial.CropX, 0f, 1f),
            CropY = Math.Clamp(initial.CropY, 0f, 1f)
        };

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 470);

        preview.Location = new Point(18, 18);
        preview.Size = new Size(624, 330);
        preview.BackColor = Color.FromArgb(24, 26, 34);
        preview.Paint += PaintPreview;
        preview.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || asset.Fit != SkinImageFit.Cover)
                return;

            dragging = true;
            dragPoint = e.Location;
            preview.Cursor = Cursors.SizeAll;
        };
        preview.MouseMove += (_, e) =>
        {
            if (!dragging)
                return;

            Rectangle area = PreviewImageBounds();
            float scale = Math.Max(
                area.Width / (float)image.Width,
                area.Height / (float)image.Height) * asset.Zoom;
            float overflowX = image.Width * scale - area.Width;
            float overflowY = image.Height * scale - area.Height;

            if (overflowX > 0)
                asset.CropX = Math.Clamp(asset.CropX - (e.X - dragPoint.X) / overflowX, 0f, 1f);
            if (overflowY > 0)
                asset.CropY = Math.Clamp(asset.CropY - (e.Y - dragPoint.Y) / overflowY, 0f, 1f);

            dragPoint = e.Location;
            preview.Invalidate();
        };
        preview.MouseUp += (_, _) =>
        {
            dragging = false;
            preview.Cursor = asset.Fit == SkinImageFit.Cover
                ? Cursors.SizeAll
                : Cursors.Default;
        };
        Controls.Add(preview);

        Label fitLabel = new()
        {
            Text = "Image fit",
            Location = new Point(20, 365),
            AutoSize = true
        };
        Controls.Add(fitLabel);

        fit.DropDownStyle = ComboBoxStyle.DropDownList;
        fit.Items.AddRange(new object[] { "Stretch", "Cover / crop", "Tile" });
        fit.Location = new Point(20, 385);
        fit.Size = new Size(160, 28);
        fit.SelectedIndex = (int)asset.Fit;
        fit.SelectedIndexChanged += (_, _) =>
        {
            asset.Fit = (SkinImageFit)fit.SelectedIndex;
            zoom.Enabled = asset.Fit == SkinImageFit.Cover;
            preview.Cursor = zoom.Enabled ? Cursors.SizeAll : Cursors.Default;
            preview.Invalidate();
        };
        Controls.Add(fit);

        zoomLabel.Text = $"Zoom: {Math.Round(asset.Zoom * 100)}%";
        zoomLabel.Location = new Point(205, 365);
        zoomLabel.AutoSize = true;
        Controls.Add(zoomLabel);

        zoom.Minimum = 100;
        zoom.Maximum = 300;
        zoom.Value = Math.Clamp((int)Math.Round(asset.Zoom * 100), 100, 300);
        zoom.TickFrequency = 50;
        zoom.Location = new Point(200, 382);
        zoom.Size = new Size(260, 42);
        zoom.Enabled = asset.Fit == SkinImageFit.Cover;
        zoom.ValueChanged += (_, _) =>
        {
            asset.Zoom = zoom.Value / 100f;
            zoomLabel.Text = $"Zoom: {zoom.Value}%";
            preview.Invalidate();
        };
        Controls.Add(zoom);

        Label hint = new()
        {
            Text = "Drag the preview to position the crop. Zoom is available in Cover mode.",
            Location = new Point(20, 430),
            AutoSize = true
        };
        Controls.Add(hint);

        Button reset = new()
        {
            Text = "Reset crop",
            Location = new Point(475, 385),
            Size = new Size(78, 30)
        };
        reset.Click += (_, _) =>
        {
            asset.CropX = 0.5f;
            asset.CropY = 0.5f;
            asset.Zoom = 1f;
            zoom.Value = 100;
            preview.Invalidate();
        };
        Controls.Add(reset);

        Button ok = new()
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(560, 385),
            Size = new Size(80, 30)
        };
        Controls.Add(ok);

        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(560, 425),
            Size = new Size(80, 30)
        };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
        preview.Cursor = asset.Fit == SkinImageFit.Cover
            ? Cursors.SizeAll
            : Cursors.Default;
    }

    private Rectangle PreviewImageBounds()
    {
        float targetAspect = targetSize.Width / (float)Math.Max(1, targetSize.Height);
        float previewAspect = preview.ClientSize.Width / (float)Math.Max(1, preview.ClientSize.Height);

        if (previewAspect > targetAspect)
        {
            int width = (int)(preview.ClientSize.Height * targetAspect);
            return new Rectangle(
                (preview.ClientSize.Width - width) / 2,
                0,
                width,
                preview.ClientSize.Height);
        }

        int height = (int)(preview.ClientSize.Width / targetAspect);
        return new Rectangle(
            0,
            (preview.ClientSize.Height - height) / 2,
            preview.ClientSize.Width,
            height);
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(preview.BackColor);
        Rectangle area = PreviewImageBounds();

        Theme.DrawFittedImage(e.Graphics, image, area, asset);

        using Pen outline = new(Color.FromArgb(190, 210, 220, 240), 1f)
        {
            DashStyle = DashStyle.Dash
        };
        e.Graphics.DrawRectangle(outline, area);
    }
}
