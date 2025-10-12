## 1. Установить `openssl`

```bash
sudo apt update
sudo apt install openssl -y
```

## 2. Сгенерировать самоподписанный сертификат (.pfx)

```bash
# 1. Создать закрытый ключ и сертификат (срок — 10 лет)
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 \
  -nodes -keyout grpc-key.pem -out grpc-cert.pem \
  -subj "/CN=192.168.80.130"

# 2. Объединить в PFX-файл (пароль "1234" можно сменить)
openssl pkcs12 -export \
  -out grpc-cert.pfx \
  -inkey grpc-key.pem \
  -in grpc-cert.pem \
  -password pass:1234
```

> В `CN` (Common Name) необходимо указать IP или DNS-имя, по которому будет подключаться клиент.
> Например, `/CN=grpc.local` или `/CN=192.168.80.130`.

После этого появятся три файла:

```
grpc-key.pem
grpc-cert.pem
grpc-cert.pfx
```

## 3. Подвязать сертификаты можно в `appsettings.json`:

 "Certificate": {
          "Path": "grpc-cert.pfx",
          "Password": "1234"
        }