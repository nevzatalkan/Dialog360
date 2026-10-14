Diyalog 360 adında Türkçe bir raporlama asistanı yaz. Kullanıcı Türkçe soru sorar, LLM SQL üretir, kod bu SQL'i güvenle çalıştırır ve sonucu grafik, tablo ve Türkçe özetle gösterir. Ses özelliği olmasın.

Teknoloji: .NET 10 Minimal API ve SQLite; önyüz React, TypeScript, Vite, Tailwind ve Recharts. LLM için OpenAI uyumlu API kullan (varsayılan: yerel llama-server ile Qwen).

Veri: Açılışta her seferinde aynı demo banka veritabanını üret: şube, müşteri, hesap, kart, sektör, son 24 aya ait yaklaşık 150 bin kart işlemi, ürün ve kampanya. Müşterinin adı, TCKN'si ve telefonu kişisel veri sayılsın. Veride mevsimsellik ve birkaç ilginç anomali olsun.

LLM: Sistem prompt'u metin dosyalarından gelsin: kurallar, şema ve açıklamaları, iş sözlüğü, örnekler. Model yalnızca JSON döndürsün: tür (rapor / netleştirme / ret), başlık, SQL, açıklama, varsayımlar, netleştirme sorusu ve seçenekler. Tarih filtrelerinde date('now') kullanılsın; takip sorularında önceki SQL temel alınsın.

Güvenlik:
- Veritabanı bağlantısı salt okunur olsun.
- SQLite authorizer ile yalnızca SELECT, şemadaki tablolar ve izinli fonksiyonlar geçsin; kişisel veri kolonları hiçbir yerde kullanılamasın; tek ifadeye izin verilsin.
- Sorgu 15 saniyede kesilsin, en fazla 1000 satır dönsün.
- SQL hatalıysa hata mesajı modele geri verilsin, en fazla 3 deneme yapılsın.
- LLM'e hiçbir satır verisi gönderilmesin.
- Tek kullanıcılı, şifreli giriş olsun.

Grafik türünü (gösterge, çubuk, çizgi ya da tablo) ve sayısal Türkçe özeti LLM değil kod, sonucun yapısına bakarak belirlesin. Aynı soru tekrar sorulursa cevap önbellekten gelsin; her sorgu loglansın.

Arayüz:
- Sohbet ekranı ve öneri soruları.
- Netleştirme sorularında tıklanabilir seçenek düğmeleri.
- Rapor kartında Grafik, Tablo ve SQL sekmeleri; CSV indirme.
- SQL'i düzenleyip yeniden çalıştırma; sorgu planı ve süreler.
- Veri kataloğu ve geçmiş sohbetler.

Güvenlik doğrulayıcısı için testler ve doğruluğu ölçen bir eval seti ekle. README'ye çalıştırma adımlarını yaz.
