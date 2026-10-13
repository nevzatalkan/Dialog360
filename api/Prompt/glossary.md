- Harcama, ciro, kart harcaması = SUM(Islem.Tutar), IslemTipi = 'Harcama'
- İade = IslemTipi = 'İade' olan işlemler; net harcama = harcamalar − iadeler
- İşlem sayısı / işlem adedi = COUNT(Islem.Id), harcama ve iadeler birlikte (IslemTipi filtresi koyma); yalnızca "harcama sayısı" denirse IslemTipi = 'Harcama'
- Ortalama sepet = harcama tutarı / harcama işlem sayısı
- Aktif müşteri = son 90 günde en az bir 'Harcama' işlemi olan müşteri
- İşlem yapmayan / pasif müşteri = son 90 günde hiç işlemi olmayan müşteri (NOT EXISTS ile)
- X kart sahibi = KartTipi = 'X' olan en az bir kartı bulunan müşteri; sayarken COUNT(DISTINCT m.Id)
- Mevduat / toplam bakiye = SUM(Hesap.Bakiye), Durum = 'Aktif' ve ParaBirimi = 'TRY' (döviz bakiyeleri TL ile toplanmaz)
- Ürün sayısı = müşterinin MusteriUrun kayıt sayısı
- Döviz hesabı = HesapTuru = 'Döviz' (para birimine göre değil); TL hesabı = ParaBirimi = 'TRY'
- Kart ve hesap sayımları ("kart sayısı", "X hesabı olan müşteri") = kullanıcı aksini söylemedikçe yalnızca Durum = 'Aktif' kayıtlar; bunu varsayımlara yaz
- Şube, şehir, il, bölge → Sube tablosu (Ad, Sehir, Bolge). Müşterinin şubesi Musteri.SubeId'dir; bir işlemin şubesi, işlemi yapan kartın müşterisinin şubesidir (Islem → Kart → Musteri → Sube)
- Yaş = CAST(strftime('%Y','now') AS INTEGER) - Musteri.DogumYili
- Kampanya harcaması = kampanyanın kendi sektöründe ve kendi tarihleri içinde yapılan harcama: JOIN Kampanya ka ON i.SektorId = ka.SektorId AND date(i.IslemTarihi) BETWEEN ka.BaslangicTarihi AND ka.BitisTarihi

## Belirsiz terimler (ölçü belirtilmemişse netleştirme sor)
- "En iyi / en değerli / en önemli müşteri" → harcama tutarı mı, toplam bakiye mi, ürün sayısı mı?
- Şube veya kart için "performans", "başarı" → harcama tutarı mı, işlem sayısı mı, müşteri sayısı mı?
- "Büyük müşteri" → KOBİ segmenti mi, yüksek bakiye mi?
Soru ölçüyü zaten söylüyorsa (örn. "harcamaya göre en iyi müşteriler") netleştirme sorma.
