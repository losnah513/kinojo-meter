using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KinojoMeterShared
{
    internal sealed class MeterConsentReceipt
    {
        public string DocumentVersion { get; set; }
        public bool ServiceRiskAccepted { get; set; }
        public bool StatisticsAccepted { get; set; }
        public string AcceptedAtUtc { get; set; }
        public string InstallerVersion { get; set; }
    }

    internal static class MeterConsentContract
    {
        public const string DocumentVersion = "METER-CONSENT-2026-07-24-v1";
        public const string Aion2PolicyUrl = "https://www.plaync.com/policy/operation/aion2";
        public const string PrivacyUrl = "https://kinojo.info/pages/privacy.html";

        public const string ServiceRiskText =
            "키노조 미터는 개인이 개발·운영하는 AION2 비공식 프로그램이며 NC 또는 AION2의 공식 프로그램이나 제휴 서비스가 아닙니다.\r\n\r\n" +
            "외부 프로그램 사용은 AION2 이용약관 및 운영정책에 따라 비인가 프로그램으로 판단될 가능성이 있으며 계정 이용제한·게임정보 조정 등 불이익이 발생할 수 있습니다. 제재 여부는 NC의 정책과 판단에 따르며 키노조는 제재가 발생하지 않음을 보장하지 않습니다.\r\n\r\n" +
            "서비스와 기능은 운영·기술·보안상 사유로 변경되거나 중단될 수 있으며 긴급한 사유가 있는 경우 사전 고지 없이 종료될 수 있습니다.";

        public const string StatisticsText =
            "KINOJO Meter는 완료 전투의 통계 계산과 동일 조건 비교를 위해 이용자 본인의 전투 시각, 클래스, 콘텐츠·던전·보스·난이도, 전투력 구간, 피해량·DPS·전투시간, 프로그램 버전, 회원 및 선택 캐릭터 내부 식별값을 Server Engine에 전송·저장합니다.\r\n\r\n" +
            "이용 목적: DPS 계산과 완료 전투 검증, 동일 조건 통계·비교, 오류 분석, 서비스 품질 개선.\r\n\r\n" +
            "보유 기간: 동의 철회, 회원 탈퇴 또는 서비스 종료 시까지. 동의 철회 후 동의 이력은 분쟁 대응을 위해 회원 탈퇴 또는 서비스 종료 시까지 보관할 수 있으며, 개인을 식별할 수 없도록 집계된 통계는 유지될 수 있습니다.\r\n\r\n" +
            "동의를 거부하거나 철회할 수 있으나 다운로드와 전투 통계 업로드·비교 기능 이용이 제한됩니다. 철회는 KINOJO INFO 문의 페이지에서 요청할 수 있습니다.";

        public static string ReceiptPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "KINOJO Meter",
                    "consent.json");
            }
        }

        public static bool TryReadCurrentReceipt(out MeterConsentReceipt receipt)
        {
            receipt = null;
            try
            {
                if (!File.Exists(ReceiptPath)) return false;
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ReceiptPath));
                if (data == null) return false;
                object version;
                object risk;
                object statistics;
                if (!data.TryGetValue("documentVersion", out version) ||
                    !String.Equals(Convert.ToString(version), DocumentVersion, StringComparison.Ordinal) ||
                    !data.TryGetValue("serviceRiskAccepted", out risk) || !Convert.ToBoolean(risk) ||
                    !data.TryGetValue("statisticsAccepted", out statistics) || !Convert.ToBoolean(statistics))
                    return false;
                object acceptedAt;
                object installerVersion;
                data.TryGetValue("acceptedAtUtc", out acceptedAt);
                data.TryGetValue("installerVersion", out installerVersion);
                receipt = new MeterConsentReceipt
                {
                    DocumentVersion = Convert.ToString(version),
                    ServiceRiskAccepted = true,
                    StatisticsAccepted = true,
                    AcceptedAtUtc = Convert.ToString(acceptedAt),
                    InstallerVersion = Convert.ToString(installerVersion)
                };
                return true;
            }
            catch
            {
                receipt = null;
                return false;
            }
        }

        public static bool HasCurrentReceipt()
        {
            MeterConsentReceipt receipt;
            return TryReadCurrentReceipt(out receipt);
        }

        public static void WriteReceipt(string installerVersion)
        {
            var directory = Path.GetDirectoryName(ReceiptPath);
            if (String.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("동의 영수증 저장 경로가 올바르지 않습니다.");
            Directory.CreateDirectory(directory);
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "documentVersion", DocumentVersion },
                { "serviceRiskAccepted", true },
                { "statisticsAccepted", true },
                { "acceptedAtUtc", DateTime.UtcNow.ToString("o") },
                { "installerVersion", installerVersion ?? "" }
            };
            File.WriteAllText(ReceiptPath, new JavaScriptSerializer().Serialize(payload));
        }

        public static string DisplayText
        {
            get
            {
                return "[1. 비공식 외부 프로그램 이용 위험]\r\n\r\n" + ServiceRiskText +
                    "\r\n\r\n[2. 전투 통계 수집·이용]\r\n\r\n" + StatisticsText;
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("웹 브라우저를 열지 못했습니다.\r\n\r\n" + url, "KINOJO Meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
