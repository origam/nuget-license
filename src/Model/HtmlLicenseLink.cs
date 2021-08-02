using System.Linq;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace NugetUtility
{
    public class HtmlLicenseLink
    {
        public string url { get;}
        public string SignificantText { get; set; } // setter is needed for deserialization

        public HtmlLicenseLink(string url, string html)
        {
            this.url = url;
            SignificantText = GetSignificantText(html);
        }

        private string GetSignificantText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html;
            }

            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(html);
            HtmlNode bodyNode = doc.DocumentNode.SelectSingleNode("/html/body");
            return bodyNode == null
                ? html
                : bodyNode.InnerText.Replace("\n", "");
        }

        protected bool Equals(HtmlLicenseLink other)
        {
            return SignificantText == other.SignificantText;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((HtmlLicenseLink) obj);
        }

        public override int GetHashCode()
        {
            return (SignificantText != null ? SignificantText.GetHashCode() : 0);
        }
    }
}