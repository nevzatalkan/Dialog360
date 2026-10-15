- Harcama, ciro, kart harcaması = SUM(Islem.Tutar), IslemTipi = 'Harcama'
- İade = IslemTipi = 'İade' olan işlemler; net harcama = harcamalar − iadeler
- İşlem sayısı / işlem adedi = COUNT(Islem.Id), harcama ve iadeler birlikte (IslemTipi filtresi koyma); yalnızca "harcama sayısı" denirse IslemTipi = 'Harcama'
- Ortalama sepet (tutarı) = SUM(i.Tutar) / COUNT(i.Id), yalnızca 'Harcama' işlemleri; İŞLEM başına ortalamadır. Müşteri başına ortalamayla karıştırma, müşteriye göre gruplayan alt sorgu kullanma
- Aktif müşteri = son 90 günde en az bir 'Harcama' işlemi olan müşteri
- İşlem yapmayan / pasif müşteri = son 90 günde hiç işlemi olmayan müşteri (NOT EXISTS ile)
- X kart sahibi = KartTipi = 'X' olan en az bir kartı bulunan müşteri; sayarken COUNT(DISTINCT m.Id)
- Mevduat / toplam bakiye = SUM(Hesap.Bakiye), Durum = 'Aktif' ve ParaBirimi = 'TRY' (döviz bakiyeleri TL ile toplanmaz)
- Ürün sayısı = müşterinin MusteriUrun kayıt sayısı
- Kart ve hesap kayıtları üzerinden yapılan TÜM hesaplar (kart/hesap sayısı, "X kartı/hesabı olan müşteri", kart limiti ortalaması veya toplamı, bakiye) = kullanıcı aksini söylemedikçe yalnızca Durum = 'Aktif' kayıtlar (Hesap: h.Durum = 'Aktif', Kart: k.Durum = 'Aktif'); bunu varsayımlara yaz
- Harcama, iade, işlem sayısı gibi Islem tablosundan yapılan hesaplarda kartın Durum'una bakma: iptal edilmiş kartların geçmiş işlemleri de sayılır
- Döviz hesabı = HesapTuru = 'Döviz' ve Durum = 'Aktif' (USD ve EUR'nun ikisi birden; ParaBirimi = 'USD' ile daraltma); döviz bakiyelerini para birimine göre ayrı topla
- Şube, şehir, il, bölge → Sube tablosu (Ad, Sehir, Bolge). Müşterinin şubesi Musteri.SubeId'dir; bir işlemin şubesi, işlemi yapan kartın müşterisinin şubesidir (Islem → Kart → Musteri → Sube)
- Yaş = CAST(strftime('%Y','now') AS INTEGER) - Musteri.DogumYili
- "Yaşı X-Y arası olan" = CAST(strftime('%Y','now') AS INTEGER) - m.DogumYili BETWEEN X AND Y; "X ve üzeri" = >= X, "Y ve altı" = <= Y
- Kadın / erkek müşteri = Musteri.Cinsiyet = 'Kadın' / 'Erkek'
- "Müşteri olma süresi X-Y yıl" = (julianday('now') - julianday(m.KayitTarihi)) / 365.25 BETWEEN X AND Y
- "Toplam TL mevduat bakiyesi X-Y TL arası olan müşteri" = müşterinin aktif TRY hesaplarının bakiye toplamı (alt sorguda GROUP BY h.MusteriId HAVING SUM(h.Bakiye) BETWEEN X AND Y)
- "X kartı bulunan", "X hesabı bulunan", "X ürününe sahip", "X sektöründe kart harcaması yapan", "X kanalını kullanan" = müşteri bazlı EXISTS koşulları (kart ve hesapta yalnızca aktif kayıtlar)
- Pay / oran (yüzde) = ROUND(100.0 * pay / payda, 1)
- Yüzde değişim = ROUND(100.0 * (yeni - eski) / eski, 1)
- Müşteri başına ortalama harcama = aynı gruplama içinde (ay, segment, şube...) doğrudan SUM(i.Tutar) / COUNT(DISTINCT m.Id). Müşteri toplamlarını alt sorguda hesaplayıp AVG alma; ay kırılımında her ayın kendi müşterileri ve harcaması kullanılır
- Hafta sonu = strftime('%w', i.IslemTarihi) IN ('0','6') (0 = Pazar, 6 = Cumartesi); hafta içi = diğer günler
- Müşterinin harcaması / işlemleri = müşterinin tüm kartlarındaki işlemler (Islem → Kart → k.MusteriId)
- İlk N gün (bir başlangıç tarihinden itibaren) = date(tarih) >= başlangıç AND date(tarih) < date(başlangıç, '+N days')
- Bir olaydan (örn. müşterinin ilk kampanya harcaması, zamanı X) sonraki N gün = i.IslemTarihi > X AND date(i.IslemTarihi) <= date(X, '+N days'); pencere olayın hemen ardından başlar (0-N. gün), N gün sonra başlamaz
- Saatli tarih kolonu (IslemTarihi 'YYYY-MM-DD HH:MM:SS') bir tarihle <= veya BETWEEN ile kıyaslanırken kolonu date() içine al: date(i.IslemTarihi) <= date(X, '+30 days'); aksi halde son günün işlemleri dışarıda kalır
- "Segmentasyon yap", "müşterileri davranışına göre grupla" = müşterileri ilgili davranışa göre CASE WHEN ile gruplara ayır (örn. hiç işlem yok / yalnızca kampanyalı / yalnızca kampanyasız / karma) ve her grubun müşteri sayısını ver; davranışı olmayan müşterileri de LEFT JOIN ile dahil et. Yalnızca "segmentlere göre" denirse Musteri.Segment kullan; ikisi birlikte istenirse ikisine göre grupla
- Müşterinin ilk kampanya harcaması = müşterinin kampanya harcaması tanımına uyan işlemleri içinde MIN(i.IslemTarihi); kampanyalı işlem = işlemin sektöründe ve tarihinde geçerli bir kampanya var (EXISTS ile Kampanya)
- Aylık değişim (zaman serisi: satırlar ardışık aylar) = LAG(ölçü) OVER (ORDER BY Ay); listedeki ilk ayın değişimi boş (NULL) kalır
- İki dönemi kategoriye göre karşılaştırma (geçen ay ile önceki ay şube/sektör bazında, bu yıl ile geçen yıl) = her dönemi SUM(CASE WHEN <dönem koşulu> THEN i.Tutar ELSE 0 END) ile ayrı kolonda hesapla, sonra yüzde değişimi al. Burada LAG KULLANMA: LAG kategorileri birbiriyle karşılaştırır, dönemleri değil
- Kampanya harcaması = kampanyanın kendi sektöründe ve kendi tarihleri içinde yapılan harcama: JOIN Kampanya ka ON i.SektorId = ka.SektorId AND date(i.IslemTarihi) BETWEEN ka.BaslangicTarihi AND ka.BitisTarihi
- Kampanya öncesi dönem = kampanya başlangıcından hemen önceki, kampanya süresiyle aynı gün sayısındaki dönem: date(i.IslemTarihi) >= date(ka.BaslangicTarihi, '-' || CAST(julianday(ka.BitisTarihi) - julianday(ka.BaslangicTarihi) + 1 AS INTEGER) || ' days') AND date(i.IslemTarihi) < ka.BaslangicTarihi

## Belirsiz terimler (ölçü belirtilmemişse netleştirme sor)
Yalnızca aşağıdaki listede geçen belirsiz sıfatlar (iyi, değerli, önemli, büyük, performans, başarı) için ve ölçü söylenmemişse tur "netlestirme"dir; bu durumda kendin bir ölçü varsayıp rapor üretme. Soruda bir dönem ("geçen ay") geçmesi ölçüyü belirlemez.
- "En iyi / en değerli / en önemli müşteri" → harcama tutarı mı, toplam bakiye mi, ürün sayısı mı?
- Şube veya kart için "performans", "başarı" → harcama tutarı mı, işlem sayısı mı, müşteri sayısı mı?
- "Büyük / en büyük müşteri" → harcama tutarı mı, yüksek bakiye mi, KOBİ segmenti mi?
Soru ölçüyü zaten söylüyorsa (örn. "harcamaya göre en iyi müşteriler") netleştirme sorma.
"En çok / en fazla / en yüksek / en düşük / en az" belirsiz DEĞİLDİR; ölçü cümlenin içindedir, netleştirme sorma ve doğrudan rapor üret:
- "En çok müşteriye satılmış ürün" → ürünü alan müşteri sayısı (COUNT)
- "En çok harcama yapan müşteri/şube/sektör" → harcama tutarı (SUM(i.Tutar))
- "En çok işlem yapılan kanal" → işlem sayısı; "en yüksek bakiye" → bakiye toplamı
