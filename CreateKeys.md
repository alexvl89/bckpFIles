## 1. ���������� `openssl`

```bash
sudo apt update
sudo apt install openssl -y
```

## 2. ������������� ��������������� ���������� (.pfx)

```bash
# 1. ������� �������� ���� � ���������� (���� � 10 ���)
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 \
  -nodes -keyout grpc-key.pem -out grpc-cert.pem \
  -subj "/CN=192.168.80.130"

# 2. ���������� � PFX-���� (������ "1234" ����� �������)
openssl pkcs12 -export \
  -out grpc-cert.pfx \
  -inkey grpc-key.pem \
  -in grpc-cert.pem \
  -password pass:1234
```

> � `CN` (Common Name) ���������� ������� IP ��� DNS-���, �� �������� ����� ������������ ������.
> ��������, `/CN=grpc.local` ��� `/CN=192.168.80.130`.

����� ����� �������� ��� �����:

```
grpc-key.pem
grpc-cert.pem
grpc-cert.pfx
```

## 3. ��������� ����������� ����� � `appsettings.json`:

 "Certificate": {
          "Path": "grpc-cert.pfx",
          "Password": "1234"
        }