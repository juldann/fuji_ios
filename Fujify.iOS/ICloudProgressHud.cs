using System;
using CoreGraphics;
using Foundation;
using UIKit;

namespace Fujify.iOS;

/// <summary>
/// Native iOS modal HUD showing real-time progress while downloading photos from iCloud.
/// Designed according to Apple Human Interface Guidelines with dark blur and emerald accents.
/// </summary>
public class ICloudProgressHud
{
    private UIView? _overlayView;
    private UIVisualEffectView? _hudCard;
    private UIActivityIndicatorView? _spinner;
    private UILabel? _titleLabel;
    private UILabel? _statusLabel;
    private UIProgressView? _progressBar;
    private UILabel? _percentLabel;
    private UIButton? _cancelButton;
    private volatile bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public void Show(UIViewController parent, int totalCount)
    {
        _isCancelled = false;

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            var window = parent.View?.Window ?? UIApplication.SharedApplication.KeyWindow;
            var parentView = window ?? parent.View;
            if (parentView == null) return;

            // Ensure any existing HUD is removed
            _overlayView?.RemoveFromSuperview();

            // Dim backdrop overlay
            _overlayView = new UIView(parentView.Bounds)
            {
                BackgroundColor = UIColor.FromRGBA(0f, 0f, 0f, 0.45f),
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                Alpha = 0f
            };

            // Dark frosted glass container
            UIBlurEffect blurEffect = UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemMaterialDark);
            _hudCard = new UIVisualEffectView(blurEffect)
            {
                ClipsToBounds = true,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            _hudCard.Layer.CornerRadius = 18f;
            _hudCard.Layer.BorderWidth = 1f;
            _hudCard.Layer.BorderColor = UIColor.FromRGBA(1f, 1f, 1f, 0.12f).CGColor;

            // Emerald spinner
            _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
            {
                Color = UIColor.FromRGB(0x34, 0xD3, 0x99),
                TranslatesAutoresizingMaskIntoConstraints = false,
                HidesWhenStopped = true
            };
            _spinner.StartAnimating();

            // Header title
            _titleLabel = new UILabel
            {
                Text = "Mengunduh dari iCloud...",
                TextColor = UIColor.White,
                Font = UIFont.BoldSystemFontOfSize(15f),
                TextAlignment = UITextAlignment.Center,
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            // Detail / filename label
            _statusLabel = new UILabel
            {
                Text = totalCount > 1 ? $"Foto 1 dari {totalCount}" : "Menyiapkan foto...",
                TextColor = UIColor.FromRGBA(1f, 1f, 1f, 0.72f),
                Font = UIFont.SystemFontOfSize(12f),
                TextAlignment = UITextAlignment.Center,
                LineBreakMode = UILineBreakMode.MiddleTruncation,
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            // Progress bar
            _progressBar = new UIProgressView(UIProgressViewStyle.Default)
            {
                ProgressTintColor = UIColor.FromRGB(0x34, 0xD3, 0x99),
                TrackTintColor = UIColor.FromRGBA(1f, 1f, 1f, 0.15f),
                Progress = 0f,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            _progressBar.Layer.CornerRadius = 2.5f;
            _progressBar.ClipsToBounds = true;

            // Percentage label
            _percentLabel = new UILabel
            {
                Text = "0%",
                TextColor = UIColor.FromRGB(0x34, 0xD3, 0x99),
                Font = UIFont.BoldSystemFontOfSize(12f),
                TextAlignment = UITextAlignment.Center,
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            // Cancel action button
            _cancelButton = UIButton.FromType(UIButtonType.System);
            _cancelButton.SetTitle("Batal", UIControlState.Normal);
            _cancelButton.SetTitleColor(UIColor.FromRGBA(1f, 1f, 1f, 0.65f), UIControlState.Normal);
            if (_cancelButton.TitleLabel != null)
            {
                _cancelButton.TitleLabel.Font = UIFont.SystemFontOfSize(13f);
            }
            _cancelButton.TranslatesAutoresizingMaskIntoConstraints = false;
            _cancelButton.TouchUpInside += (s, e) =>
            {
                _isCancelled = true;
                if (_statusLabel != null)
                {
                    _statusLabel.Text = "Membatalkan...";
                }
                _cancelButton.Enabled = false;
            };

            var contentView = _hudCard.ContentView;
            contentView.AddSubview(_spinner);
            contentView.AddSubview(_titleLabel);
            contentView.AddSubview(_statusLabel);
            contentView.AddSubview(_progressBar);
            contentView.AddSubview(_percentLabel);
            contentView.AddSubview(_cancelButton);

            _overlayView.AddSubview(_hudCard);
            parentView.AddSubview(_overlayView);

            // Auto Layout Constraints
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _hudCard.CenterXAnchor.ConstraintEqualTo(parentView.CenterXAnchor),
                _hudCard.CenterYAnchor.ConstraintEqualTo(parentView.CenterYAnchor),
                _hudCard.WidthAnchor.ConstraintEqualTo(270f),

                _spinner.TopAnchor.ConstraintEqualTo(contentView.TopAnchor, 18f),
                _spinner.CenterXAnchor.ConstraintEqualTo(contentView.CenterXAnchor),

                _titleLabel.TopAnchor.ConstraintEqualTo(_spinner.BottomAnchor, 10f),
                _titleLabel.LeadingAnchor.ConstraintEqualTo(contentView.LeadingAnchor, 16f),
                _titleLabel.TrailingAnchor.ConstraintEqualTo(contentView.TrailingAnchor, -16f),

                _statusLabel.TopAnchor.ConstraintEqualTo(_titleLabel.BottomAnchor, 4f),
                _statusLabel.LeadingAnchor.ConstraintEqualTo(contentView.LeadingAnchor, 16f),
                _statusLabel.TrailingAnchor.ConstraintEqualTo(contentView.TrailingAnchor, -16f),

                _progressBar.TopAnchor.ConstraintEqualTo(_statusLabel.BottomAnchor, 14f),
                _progressBar.LeadingAnchor.ConstraintEqualTo(contentView.LeadingAnchor, 20f),
                _progressBar.TrailingAnchor.ConstraintEqualTo(contentView.TrailingAnchor, -20f),
                _progressBar.HeightAnchor.ConstraintEqualTo(5f),

                _percentLabel.TopAnchor.ConstraintEqualTo(_progressBar.BottomAnchor, 6f),
                _percentLabel.CenterXAnchor.ConstraintEqualTo(contentView.CenterXAnchor),

                _cancelButton.TopAnchor.ConstraintEqualTo(_percentLabel.BottomAnchor, 6f),
                _cancelButton.CenterXAnchor.ConstraintEqualTo(contentView.CenterXAnchor),
                _cancelButton.BottomAnchor.ConstraintEqualTo(contentView.BottomAnchor, -12f),
                _cancelButton.HeightAnchor.ConstraintEqualTo(32f)
            });

            UIView.Animate(0.25, () =>
            {
                _overlayView.Alpha = 1f;
            });
        });
    }

    public void Update(int currentIndex, int totalCount, string fileName, double itemFraction)
    {
        if (_isCancelled) return;

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_statusLabel != null)
            {
                string name = string.IsNullOrEmpty(fileName) ? "" : $": {fileName}";
                _statusLabel.Text = totalCount > 1
                    ? $"Foto {currentIndex} dari {totalCount}{name}"
                    : $"Mengunduh{name}";
            }

            double totalFraction = 0.0;
            if (totalCount > 0)
            {
                totalFraction = Math.Clamp(((currentIndex - 1) + itemFraction) / (double)totalCount, 0.0, 1.0);
            }

            if (_progressBar != null)
            {
                _progressBar.SetProgress((float)totalFraction, true);
            }

            if (_percentLabel != null)
            {
                _percentLabel.Text = $"{(int)(totalFraction * 100.0)}%";
            }
        });
    }

    public void Dismiss()
    {
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_overlayView != null)
            {
                var viewToDismiss = _overlayView;
                _overlayView = null;
                _hudCard = null;

                UIView.Animate(0.2, () =>
                {
                    viewToDismiss.Alpha = 0f;
                }, () =>
                {
                    viewToDismiss.RemoveFromSuperview();
                });
            }
        });
    }
}
