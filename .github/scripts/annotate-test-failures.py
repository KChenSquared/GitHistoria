"""Prints a GitHub Actions error annotation for each failed test in the given .trx files."""
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# xUnit's messages carry characters such as arrows that a Windows console encoding cannot print.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def escape(text, limit):
    text = text.strip()[:limit]
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


for path in sys.argv[1:]:
    for result in ET.parse(path).getroot().iter(NS + "UnitTestResult"):
        if result.get("outcome") != "Failed":
            continue
        message = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}Message", default="")
        trace = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}StackTrace", default="")
        name = escape(result.get("testName", "?"), 200).replace(",", "%2C").replace(":", "%3A")
        print(f"::error title={name}::{escape(message, 1500)}%0A{escape(trace, 1500)}")
