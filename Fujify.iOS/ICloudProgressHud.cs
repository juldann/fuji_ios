using System;
using CoreGraphics;
using Foundation;
using UIKit;

namespace Fujify.iOS;

/// <summary>
/// Bulletproof native iOS modal HUD implemented as a transparent OverFullScreen UIViewController.
/// Complies with Apple Human Interface Guidelines with dark frosted blur and emerald accents.
/// </summary>
public class ICloudProgressHud
{
    private HudModalViewController? _controller;
    private volatile bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public void Show(UIViewController parent, int totalCount)
    {
        _isCancelled = false;

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try
            {
                // Find active presenting controller
                var presenter = parent;
                while (presenter.PresentedViewController != null && !presenter.PresentedViewController.IsBeingDismissed)
                {
                    presenter = presenter.PresentedViewController;
                }

                _controller = new HudModalViewController(totalCount, () =>
                {
                    _isCancelled = true;
                });

                presenter.PresentViewController(_controller, true, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ICloudProgressHud] Error presenting HUD: {ex.Message}");
            }
        });
    }

    public void Update(int currentIndex, int totalCount, string fileName, double itemFraction)
    {
        if (_isCancelled) return;

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _controller?.UpdateProgress(currentIndex, totalCount, fileName, itemFraction);
        });
    }

    public void Dismiss()
    {
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (_controller != null && !_controller.IsBeingDismissed)
                {
                    _controller.DismissViewController(true, null);
                    _controller = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ICloudProgressHud] Error dismissing HUD: {ex.Message}");
            }
        });
    }

    private class HudModalViewController : UIViewController
    {
        private readonly int _initialCount;
        private readonly Action _onCancel;
        private UIVisualEffectView? _hudCard;
        private UIActivityIndicatorView? _spinner;
        private UILabel? _titleLabel;
        private UILabel? _statusLabel;
        private UIProgressView? _progressBar;
        private UILabel? _percentLabel;
        private UIButton? _cancelButton;

        public HudModalViewController(int initialCount, Action onCancel)
        {
            _initialCount = initialCount;
            _onCancel = onCancel;
            ModalPresentationStyle = UIModalPresentationStyle.OverFullScreen;
            ModalTransitionStyle = UIModalTransitionStyle.CrossDissolve;
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            if (View == null) return;

            View.BackgroundColor = UIColor.FromRGBA(0f, 0f, 0f, 0.55f);

            // Frosted blur container
            UIBlurEffect blurEffect = UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemMaterialDark);
            _hudCard = new UIVisualEffectView(blurEffect)
            {
                ClipsToBounds = true,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            _hudCard.Layer.CornerRadius = 20f;
            _hudCard.Layer.BorderWidth = 1f;
            _hudCard.Layer.BorderColor = UIColor.FromRGBA(1f, 1f, 1f, 0.15f).CGColor;

            // Emerald spinner
            _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
            {
                Color = UIColor.FromRGB(0x34, 0xD3, 0x99),
                TranslatesAutoresizingMaskIntoConstraints = false,
                HidesWhenStopped = true
            };
            _spinner.StartAnimating();

            // Title
            _titleLabel = new UILabel
            {
                Text = "Mengunduh dari iCloud...",
                TextColor = UIColor.White,
                Font = UIFont.BoldSystemFontOfSize(15f),
                TextAlignment = UITextAlignment.Center,
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            // Status label
            _statusLabel = new UILabel
            {
                Text = _initialCount > 1 ? $"Foto 1 dari {_initialCount}" : "Menghubungkan ke foto...",
                TextColor = UIColor.FromRGBA(1f, 1f, 1f, 0.75f),
                Font = UIFont.SystemFontOfSize(12f),
                TextAlignment = UITextAlignment.Center,
                LineBreakMode = UILineBreakMode.MiddleTruncation,
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            // Emerald Progress bar
            _progressBar = new UIProgressView(UIProgressViewStyle.Default)
            {
                ProgressTintColor = UIColor.FromRGB(0x34, 0xD3, 0x99),
                TrackTintColor = UIColor.FromRGBA(1f, 1f, 1f, 0.2f),
                Progress = 0f,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            _progressBar.Layer.CornerRadius = 3f;
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

            // Cancel button
            _cancelButton = UIButton.FromType(UIButtonType.System);
            _cancelButton.SetTitle("Batal", UIControlState.Normal);
            _cancelButton.SetTitleColor(UIColor.FromRGBA(1f, 1f, 1f, 0.7f), UIControlState.Normal);
            if (_cancelButton.TitleLabel != null)
            {
                _cancelButton.TitleLabel.Font = UIFont.SystemFontOfSize(13f);
            }
            _cancelButton.TranslatesAutoresizingMaskIntoConstraints = false;
            _cancelButton.TouchUpInside += (s, e) =>
            {
                if (_statusLabel != null)
                {
                    _statusLabel.Text = "Membatalkan unduhan...";
                }
                _cancelButton.Enabled = false;
                _onCancel();
            };

            var contentView = _hudCard.ContentView;
            contentView.AddSubview(_spinner);
            contentView.AddSubview(_titleLabel);
            contentView.AddSubview(_statusLabel);
            contentView.AddSubview(_progressBar);
            contentView.AddSubview(_percentLabel);
            contentView.AddSubview(_cancelButton);

            View.AddSubview(_hudCard);

            // Safe, direct parent-child auto layout constraints
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _hudCard.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
                _hudCard.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor),
                _hudCard.WidthAnchor.ConstraintEqualTo(280f),

                _spinner.TopAnchor.ConstraintEqualTo(contentView.TopAnchor, 20f),
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
                _progressBar.HeightAnchor.ConstraintEqualTo(6f),

                _percentLabel.TopAnchor.ConstraintEqualTo(_progressBar.BottomAnchor, 6f),
                _percentLabel.CenterXAnchor.ConstraintEqualTo(contentView.CenterXAnchor),

                _cancelButton.TopAnchor.ConstraintEqualTo(_percentLabel.BottomAnchor, 6f),
                _cancelButton.CenterXAnchor.ConstraintEqualTo(contentView.CenterXAnchor),
                _cancelButton.BottomAnchor.ConstraintEqualTo(contentView.BottomAnchor, -12f),
                _cancelButton.HeightAnchor.ConstraintEqualTo(32f)
            });
        }

        public void UpdateProgress(int currentIndex, int totalCount, string fileName, double itemFraction)
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
        }
    }
}
