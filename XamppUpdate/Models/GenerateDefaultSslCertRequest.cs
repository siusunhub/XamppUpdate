using System;
using System.Text;

namespace XamppUpdate.Models
{
    public class GenerateDefaultSslCertRequest
    {
        public string ApachePath { get; set; } = @"C:\xampp\apache";
        public string Country { get; set; } = "US";
        public string State { get; set; } = "Local";
        public string Locality { get; set; } = "Local";
        public string Organization { get; set; } = "Development";
        public string OrganizationalUnit { get; set; } = "IT";
        public string CommonName { get; set; } = "localhost";
        public int Days { get; set; } = 3650;
        public int KeyBits { get; set; } = 2048;
        public string DnsNames { get; set; } = "localhost";
        public string IpAddresses { get; set; } = "127.0.0.1";

        public string RelativeKeyPath { get; set; } = @"conf\ssl.key\server.key";
        public string RelativeCertPath { get; set; } = @"conf\ssl.crt\server.crt";

        public string GenerateConfigFileContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[req]");
            sb.AppendLine($"default_bits = {KeyBits}");
            sb.AppendLine("prompt = no");
            sb.AppendLine("default_md = sha256");
            sb.AppendLine("distinguished_name = req_distinguished_name");
            sb.AppendLine("x509_extensions = v3_req");
            sb.AppendLine();
            sb.AppendLine("[req_distinguished_name]");
            sb.AppendLine($"C = {Country.Trim()}");
            sb.AppendLine($"ST = {State.Trim()}");
            sb.AppendLine($"L = {Locality.Trim()}");
            sb.AppendLine($"O = {Organization.Trim()}");
            sb.AppendLine($"OU = {OrganizationalUnit.Trim()}");
            sb.AppendLine($"CN = {CommonName.Trim()}");
            sb.AppendLine();
            sb.AppendLine("[v3_req]");
            sb.AppendLine("subjectAltName = @alt_names");
            sb.AppendLine();
            sb.AppendLine("[alt_names]");

            // Parse DNS names
            var dnsList = DnsNames.Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int dnsIndex = 1;
            foreach (var dns in dnsList)
            {
                sb.AppendLine($"DNS.{dnsIndex++} = {dns}");
            }
            if (dnsIndex == 1)
            {
                sb.AppendLine("DNS.1 = localhost");
            }

            // Parse IP addresses
            var ipList = IpAddresses.Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int ipIndex = 1;
            foreach (var ip in ipList)
            {
                sb.AppendLine($"IP.{ipIndex++} = {ip}");
            }
            if (ipIndex == 1)
            {
                sb.AppendLine("IP.1 = 127.0.0.1");
            }

            return sb.ToString();
        }

        public string GetOpenSslCommandLine()
        {
            return $"bin\\openssl req -x509 -nodes -days {Days} -newkey rsa:{KeyBits} -keyout {RelativeKeyPath} -out {RelativeCertPath} -config cert_config.cnf -extensions v3_req";
        }
    }

    public class GenerateDefaultSslCertResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public SslCertificateInfo? UpdatedCertInfo { get; set; }
    }
}
