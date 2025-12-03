#!/bin/bash

# Mockoon sunucusuna environment yükleme scripti
# Kullanım: ./upload-to-mockoon.sh

MOCKOON_URL="https://poc-mockoon.apps.nonprod.ebt.bank"
JSON_FILE="migration-api.json"

echo "🚀 Mockoon sunucusuna bağlanılıyor..."
echo "📍 Sunucu: $MOCKOON_URL"
echo "📄 Dosya: $JSON_FILE"
echo ""

# Sunucu durumunu kontrol et
echo "1️⃣ Sunucu durumu kontrol ediliyor..."
if curl -k -s -f "$MOCKOON_URL/health" > /dev/null 2>&1; then
    echo "✅ Sunucu aktif"
else
    echo "⚠️  Sunucu health check başarısız, devam ediliyor..."
fi

echo ""
echo "2️⃣ Environment yükleniyor..."

# Environment'ı POST et
RESPONSE=$(curl -k -s -w "\n%{http_code}" -X POST \
    "$MOCKOON_URL/api/environments" \
    -H "Content-Type: application/json" \
    -d @"$JSON_FILE" 2>&1)

HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | head -n-1)

echo "📡 HTTP Durum Kodu: $HTTP_CODE"
echo ""

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
    echo "✅ Başarılı! Environment yüklendi."
    echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
elif [ "$HTTP_CODE" = "409" ]; then
    echo "ℹ️  Environment zaten mevcut, güncelleme deneniyor..."
    
    # UUID'yi dosyadan al
    UUID=$(jq -r '.uuid' "$JSON_FILE")
    
    # PUT ile güncelle
    RESPONSE=$(curl -k -s -w "\n%{http_code}" -X PUT \
        "$MOCKOON_URL/api/environments/$UUID" \
        -H "Content-Type: application/json" \
        -d @"$JSON_FILE" 2>&1)
    
    HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
    BODY=$(echo "$RESPONSE" | head -n-1)
    
    if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "204" ]; then
        echo "✅ Environment güncellendi!"
        echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
    else
        echo "❌ Güncelleme başarısız!"
        echo "$BODY"
    fi
else
    echo "❌ Yükleme başarısız!"
    echo "$BODY"
fi

echo ""
echo "🏁 İşlem tamamlandı."

