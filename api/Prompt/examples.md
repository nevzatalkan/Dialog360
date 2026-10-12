### Örnek 1
Yeni mesaj: Geçen ay sektörlere göre toplam kart harcaması
{"tur":"rapor","baslik":"Geçen Ay Sektörlere Göre Harcama","sql":"SELECT s.Ad AS [Sektör], ROUND(SUM(i.Tutar), 2) AS [Toplam Harcama] FROM Islem i JOIN Sektor s ON i.SektorId = s.Id WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','start of month','-1 month') AND i.IslemTarihi < date('now','localtime','start of month') GROUP BY s.Ad ORDER BY [Toplam Harcama] DESC","aciklama":"Geçen takvim ayındaki kart harcamaları sektörlere göre toplanıp büyükten küçüğe sıralandı.","varsayimlar":["Geçen ay = bir önceki takvim ayı","İadeler hariç tutuldu"],"netlestirme_sorusu":"","secenekler":[]}

### Örnek 2 (takip mesajı)
Sohbet geçmişi: Örnek 1'deki soru ve SQL
Yeni mesaj: Bunu Ankara şubeleri için son 3 ay aylık göster
{"tur":"rapor","baslik":"Ankara Şubeleri Aylık Sektör Harcaması","sql":"SELECT strftime('%Y-%m', i.IslemTarihi) AS [Ay], s.Ad AS [Sektör], ROUND(SUM(i.Tutar), 2) AS [Toplam Harcama] FROM Islem i JOIN Sektor s ON i.SektorId = s.Id JOIN Kart k ON i.KartId = k.Id JOIN Musteri m ON k.MusteriId = m.Id JOIN Sube sb ON m.SubeId = sb.Id WHERE i.IslemTipi = 'Harcama' AND sb.Sehir = 'Ankara' AND i.IslemTarihi >= date('now','localtime','start of month','-3 months') AND i.IslemTarihi < date('now','localtime','start of month') GROUP BY [Ay], s.Ad ORDER BY [Ay], [Toplam Harcama] DESC","aciklama":"Ankara'daki şubelerin müşterilerinin kart harcamaları, son üç ayda ay ay ve sektör sektör toplandı.","varsayimlar":["Son 3 ay = tamamlanmış son 3 takvim ayı","İşlemin şubesi, kart sahibinin bağlı olduğu şube kabul edildi"],"netlestirme_sorusu":"","secenekler":[]}

### Örnek 3
Yeni mesaj: En iyi müşterilerimiz kimler?
{"tur":"netlestirme","baslik":"","sql":"","aciklama":"","varsayimlar":[],"netlestirme_sorusu":"\"En iyi\" müşteriyi hangi ölçüye göre belirleyelim?","secenekler":["Kart harcaması tutarına göre","Toplam mevduat bakiyesine göre","Sahip olunan ürün sayısına göre"]}

### Örnek 4 (netleştirme cevabı)
Sohbet geçmişi: Örnek 3'teki soru ve netleştirme
Yeni mesaj: Kart harcaması tutarına göre
{"tur":"rapor","baslik":"Harcamaya Göre En İyi 10 Müşteri","sql":"SELECT m.Id AS [Müşteri No], m.Segment AS [Segment], sb.Sehir AS [Şehir], ROUND(SUM(i.Tutar), 2) AS [Toplam Harcama] FROM Islem i JOIN Kart k ON i.KartId = k.Id JOIN Musteri m ON k.MusteriId = m.Id JOIN Sube sb ON m.SubeId = sb.Id WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','-12 months') GROUP BY m.Id, m.Segment, sb.Sehir ORDER BY [Toplam Harcama] DESC LIMIT 10","aciklama":"Son 12 aydaki kart harcaması en yüksek 10 müşteri, müşteri numarası, segmenti ve şehriyle listelendi.","varsayimlar":["Dönem belirtilmediği için bugünden geriye son 12 ay alındı","Kişisel veri olduğu için müşteri adı yerine müşteri numarası gösterildi"],"netlestirme_sorusu":"","secenekler":[]}

### Örnek 5
Yeni mesaj: Son 90 günde hiç işlem yapmayan Gold kart sahibi kaç müşteri var?
{"tur":"rapor","baslik":"İşlem Yapmayan Gold Kart Sahipleri","sql":"SELECT COUNT(DISTINCT m.Id) AS [Müşteri Sayısı] FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id WHERE k.KartTipi = 'Gold' AND NOT EXISTS (SELECT 1 FROM Kart k2 JOIN Islem i ON i.KartId = k2.Id WHERE k2.MusteriId = m.Id AND i.IslemTarihi >= date('now','localtime','-90 days'))","aciklama":"Gold kartı olan müşterilerden, hiçbir kartıyla son 90 günde işlem yapmamış olanlar sayıldı.","varsayimlar":["Müşterinin diğer kartlarındaki işlemler de dikkate alındı"],"netlestirme_sorusu":"","secenekler":[]}

### Örnek 6
Yeni mesaj: Segmentlere göre müşteri sayısı ve TL mevduat toplamı
{"tur":"rapor","baslik":"Segmentlere Göre Müşteri ve Mevduat","sql":"SELECT m.Segment AS [Segment], COUNT(DISTINCT m.Id) AS [Müşteri Sayısı], ROUND(SUM(h.Bakiye), 2) AS [TL Mevduat] FROM Musteri m JOIN Hesap h ON h.MusteriId = m.Id WHERE h.Durum = 'Aktif' AND h.ParaBirimi = 'TRY' GROUP BY m.Segment ORDER BY [TL Mevduat] DESC","aciklama":"Her segmentteki müşteri sayısı ve bu müşterilerin aktif TL hesaplarındaki toplam bakiye hesaplandı.","varsayimlar":["Döviz hesapları ve kapalı hesaplar hariç tutuldu"],"netlestirme_sorusu":"","secenekler":[]}

### Örnek 7
Yeni mesaj: Tüm işlem kayıtlarını sil
{"tur":"ret","baslik":"","sql":"","aciklama":"Diyalog 360 yalnızca rapor üretir; veritabanında silme veya değişiklik yapamaz.","varsayimlar":[],"netlestirme_sorusu":"","secenekler":[]}
