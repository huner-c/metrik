# Metrik

![Metrik hız göstergesi](assets/metrik.png)

Metrik, Windows’ta oyun oynarken FPS, ortalama kare süresi ve sistem ölçümlerini gösterir. Oyun içi göstergeyi gizleyip aynı değerleri bir Android tabletten USB kablosuyla veya yerel ağ üzerinden takip edebilirsin. USB bağlantısı internet erişimi gerektirmez.

## İndir

| Uygulama | Gereksinim |
| --- | --- |
| [Metrik-Windows-x64.zip](https://github.com/huner-c/metrik/releases/latest/download/Metrik-Windows-x64.zip) | Windows x64 ve [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| [Metrik-Tablet.apk](https://github.com/huner-c/metrik/releases/latest/download/Metrik-Tablet.apk) | Android 4.4 ve üzeri |

Windows ZIP’ini bir klasöre çıkart ve `Metrik.exe` dosyasını aç. Tabletten izleyeceksen APK’yı Android cihazına kur ve PC’de Metrik’i açık tut.

## Windows’ta kullanım

- Oyunu pencere veya **Kenarlıksız Tam Ekran (Borderless)** modunda öne getir. DirectX kareleri görülünce panel oyun alanının sol üstünde açılır.
- `Ctrl + Shift + F10` oyun göstergesini açıp kapatır. Metrik’i küçültünce uygulama bildirim alanına geçer.
- **Görünen değerler** bölümünden gösterilecek alanları seç. **Gösterge boyutu** ile yazıyı %50–%200 arasında ölçekle.
- **Önizlemeyi aç** ile göstergeyi masaüstünde dene. Önizlemede FPS ve kare süresi örnektir. Ekran kartı değerleri canlıdır.
- FPS erişimi isteyen sistemlerde **Yönetici olarak yeniden aç** düğmesini kullan.

## Android tabletten izleme

Tablet yayını ilk açılışta kapalıdır. USB veya yerel ağ bağlantısını kurduktan sonra **Oyun göstergesini gizle** seçeneğiyle FPS ölçümünü tablette sürdürebilirsin. Panel saniyede bir güncellenir. Bağlantı kodu ve yayın tercihleri cihazlarda saklanır.

### USB kablosuyla bağlantı

1. Tabletin **Geliştirici seçenekleri** bölümünden **USB hata ayıklama**yı aç. Galaxy Tab 4’te bu menüyü görünür yapmak için **Ayarlar → Genel → Cihaz hakkında → Yapım numarası**na yedi kez dokun.
2. Tableti veri aktarabilen USB kablosuyla PC’ye bağla. Tabletin kilidini aç ve Windows bilgisayarı için gelen **USB hata ayıklamasına izin ver** istemini onayla.
3. PC’de **USB yayınını aç** düğmesine bas. Metrik var olan `adb.exe` dosyasını otomatik arar ve PC’deki ADB hizmetini gerektiğinde başlatır. Dosya seçim penceresi açılırsa Android SDK Platform-Tools klasöründeki `adb.exe` dosyasını seç. [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools)
4. Tablette **Bağlantı → USB kablosu** seçeneğini seç ve Metrik’in PC’de gösterdiği altı haneli kodla bağlan. Daha önce kaydettiğin kod aynıysa **Bağlan**’a dokunman yeterli.

USB bağlantısı çalışırken PC’nin Ethernet kablosunu çekebilir ve Wi-Fi’ı kapatabilirsin. Kabloyu çıkarıp takarsan USB yayını otomatik yeniden bağlanmayı dener.

### Yerel ağ bağlantısı

1. PC’yi ve tableti aynı yerel ağa bağla. PC Ethernet, tablet Wi-Fi kullanabilir.
2. Windows’ta **Yerel ağ yayınını aç** düğmesine bas. Ağ bağlantısı engellenirse **Yerel ağ izni ver** düğmesi bu Metrik kopyası için aynı alt ağdan UDP 47937 erişimi ekler. Windows yönetici onayı ister.
3. Tablette **Bağlantı → Yerel ağ** seçeneğini seç, PC’nin IP adresini ve Windows’ta görünen altı haneli kodu gir.

Misafir Wi-Fi ağı cihazlar arasındaki bağlantıyı engelleyebilir. USB ve yerel ağ aynı bağlantı kodunu kullanır.

## Ölçüm

| Alan | Değerler |
| --- | --- |
| Oyun | FPS, ortalama kare süresi, oyun adı ve DirectX API’si |
| Radeon GPU | Kullanım, sıcaklık, hotspot, çekirdek ve bellek saat hızları, VRAM, fan ve güç |
| CPU ve RAM | İşlemci kullanımı, Windows’un raporladığı saat hızı, mantıksal işlemci sayısı ve RAM kullanımı |

Metrik, Windows Event Tracing (ETW) ile Direct3D 9 ve DXGI (Direct3D 10/11/12) sunum olaylarını izler. Odaktaki işlemin en sık sunum yapan swap chain'ini kullanır ve son iki saniyedeki başarılı `Present` çağrıları üzerinden FPS ve kare süresini hesaplar. FPS ölçümü uygulamanın yaptığı sunumların hızıdır. Frame Generation ve monitörde gerçekten görüntülenen kare sayısı farklı olabilir.

Yeni kare verisi gecikince Metrik, yaklaşık iki saniyenin ardından son ölçümü işaretler. Beş saniye sonunda FPS ve kare süresi `—` olur. Donanım ölçümleri ayrı güncellenir ve okunamayan sensörler `—` olarak görünür.

Oyun göstergesi, fare tıklamalarını alttaki oyuna geçiren bir Windows penceresidir. Özel tam ekran modunda görünmeyebilir. Canlı GPU sensörleri AMD Radeon sürücüsünün sağladığı değerlere bağlıdır. Birden fazla kart varsa Metrik öncelikle Windows’un birincil Radeon kartını seçer.

CPU paket sıcaklığı ve gücü için isteğe bağlı olarak LibreHardwareMonitor’un `127.0.0.1` seçeneğini sunan bir sürümünü kullan. **Options → Remote Web Server** menüsünde adresi `127.0.0.1`, portu `8085` olarak ayarla ve **Run** seçeneğini aç. Bu yerel bağlantıda kimlik doğrulamayı kapalı bırak. LibreHardwareMonitor’da bulunan CPU paket sensörleri Metrik’te de görünür. [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)

## Kaynaktan derleme

.NET 10 SDK ile proje klasöründe çalıştır. Metrik açıksa yayından önce bildirim alanından **Çıkış** yap.

```powershell
dotnet publish -c Release --self-contained false -o .\dist\Metrik-Windows-USB -p:DebugType=None -p:DebugSymbols=false
Copy-Item .\README.md .\dist\Metrik-Windows-USB\README.md -Force
```

Android APK’sını derlemek için Windows’ta JDK 17 veya üstü, Android SDK platformları 19 ve 35, Build Tools 35.0.0 gerekir. `JAVA_HOME` ve `ANDROID_HOME` ayarlandıktan sonra proje klasöründe çalıştır.

```powershell
.\android\build.ps1
```

APK `android/build/Metrik-Tablet.apk` konumuna yazılır. Derleme imza parolasını sorar. Etkileşimsiz derlemelerde `METRIK_ANDROID_KEYSTORE_PASSWORD` ortam değişkenini kullanabilirsin. Aynı tablete sonraki derlemeleri güncelleme olarak kurabilmek için `android/.signing` altındaki yerel imza anahtarını ve parolasını sakla.

[Windows ETW](https://learn.microsoft.com/windows/win32/etw/configuring-and-starting-an-event-tracing-session) · [AMD Display Library](https://gpuopen-librariesandsdks.github.io/adl/) · [Windows katmanlı pencereler](https://learn.microsoft.com/windows/win32/winmsg/window-features#layered-windows)
