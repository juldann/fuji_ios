using UIKit;

namespace Fujify.iOS;

public class AdManager
{
    private static AdManager? _instance;
    public static AdManager Instance => _instance ??= new AdManager();
    public bool IsInitialized => false;

    public void Initialize(UIWindow? window) { }
    public void ShowSplashAppOpenAd() { }
    public void ShowInterstitialAd() { }
    public void SetBannerVisible(bool visible) { }
}
