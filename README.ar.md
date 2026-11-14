<p align="center"><img src="docs/site/assets/logo.png" width="112" height="112" alt="Uninstaller 77"></p>
<h1 align="center">Uninstaller 77</h1>
<p align="center">إدارة برامج ويندوز، مع سجل واضح لكل محاولة إزالة.</p>
<p align="center"><b>نسخة 1 (معاينة 1.0.3) · Windows 10 / 11 x64 · العربية والإنجليزية · MIT</b></p>
<p align="center"><a href="https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.3/Uninstaller77-Setup.exe">تحميل المثبت</a> · <a href="https://abooodhub.github.io/Uninstaller77/">صفحة البرنامج</a> · <a href="README.md">English</a></p>

## الخصائص

- **جرد البرامج:** بحث وفرز لبرامج Registry وحزم MSIX/AppX للمستخدم الحالي، مع معلومات الناشر والإصدار والحجم وأمر الإزالة عند توفرها.
- **إزالة متتابعة:** معالجة البرامج التي اخترتها واحدًا تلو الآخر، مع المرحلة الحالية والوقت وعدادات منفصلة للنجاح والفشل والتخطي.
- **نتيجة خاصة بكل برنامج:** حفظ رمز الخروج المرصود وتشخيص الفشل مع العنصر الصحيح، حتى بعد اكتمال العنصر التالي.
- **مراجعة المخلفات:** فحص المخلفات بعد الإزالة المؤكدة. يمكن تنظيف العناصر المؤهلة المحددة، مع نسخ احتياطية قابلة للاستعادة لإدخالات Registry المدعومة.
- **إضافات المتصفحات:** عرض الإضافات بحسب المتصفح والملف الشخصي، وفتح صفحة الإدارة في المتصفحات المدعومة لإجراء التغييرات.
- **بدء التشغيل ومراقبة التثبيت:** تفعيل إدخالات بدء التشغيل المؤهلة وتعطيلها، وتسجيل تغييرات التثبيت في مجلد مختار ومفتاح Registry اختياري، وتصدير التقرير.
- **تجربة سطح مكتب:** دعم RTL للعربية وLTR للإنجليزية، وتصدير CSV، وسجل محلي، وإجراء في قائمة Explorer لاختصارات التطبيقات.

[▶ مشاهدة فيديو البرنامج](https://youtu.be/bUDDOJ_9fTo)

## صور البرنامج

[افتح معرض الصور التفاعلي: 8 صور مع التكبير والتصنيف](https://abooodhub.github.io/Uninstaller77/#gallery)

<table>
<tr>
<td width="50%"><a href="docs/site/assets/programs-ar.jpg"><img src="docs/site/assets/programs-ar.jpg" width="480" alt="قائمة البرامج · لقطة تطوير"></a><br>قائمة البرامج · لقطة تطوير</td>
<td width="50%"><a href="docs/site/assets/windows-apps.png"><img src="docs/site/assets/windows-apps.png" width="480" alt="حزم Windows · لقطة تطوير"></a><br>حزم Windows · لقطة تطوير</td>
</tr>
<tr>
<td width="50%"><a href="docs/site/assets/extensions.png"><img src="docs/site/assets/extensions.png" width="480" alt="الإضافات · لقطة تطوير"></a><br>الإضافات · لقطة تطوير</td>
<td width="50%"><a href="docs/site/assets/queue.png"><img src="docs/site/assets/queue.png" width="480" alt="الطابور · بيانات اختبار"></a><br>الطابور · بيانات اختبار</td>
</tr>
<tr>
<td width="50%"><a href="docs/site/assets/results.png"><img src="docs/site/assets/results.png" width="480" alt="النتائج · بيانات اختبار"></a><br>النتائج · بيانات اختبار</td>
<td width="50%"><a href="docs/site/assets/history.png"><img src="docs/site/assets/history.png" width="480" alt="السجل · جلسة المستخدم"></a><br>السجل · جلسة المستخدم</td>
</tr>
</table>

*صور أصلية من التطوير والاختبار. يوضح المعرض سياق كل صورة. صور الطابور والنتائج تعرض بيانات اختبار مملوكة للمشروع.*

## تثبيت نسخة 1

1. حمّل [ملف التثبيت](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.3/Uninstaller77-Setup.exe) من صفحة الإصدار.
2. أغلق أي نسخة مفتوحة قبل التحديث، ثم شغّل المثبّت. المثبّت بالإنجليزية، ولغة التطبيق تُختار من الإعدادات.

تتضمن الحزمة ‎.NET 10 Desktop Runtime ومثبّت WebView2 Evergreen. هذه النسخة غير موقّعة، وما زال اختبار المثبّت ودورة مراقبة التثبيت وفحص المخلفات داخل VMware معلقًا.

قارن بصمة المثبّت مع [بصمات SHA-256 المنشورة](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.3/SHA256SUMS.txt).

```powershell
Get-FileHash .\Uninstaller77-Setup.exe -Algorithm SHA256
```

## فهم النتائج

يشغّل البرنامج مزيل التثبيت المسجّل، ثم يفحص دليل الإزالة. تعني نسبة **100%** انتهاء معالجة الطابور، وليس نجاح إزالة جميع البرامج. يمنع طلب الإيقاف بدء العنصر التالي بعد انتهاء العملية الحالية، ولا ينهي مزيل الشركة بالقوة.

يحافظ المحرك على أوامر EXE المسجّلة حتى عندما يكون اسم مفتاح Registry على شكل GUID. قد يرفض مزيل شركة أخرى العملية أو يحتاج إلى تدخلك. قد تمنع حماية Windows إزالة بعض الحزم.

## البناء والاختبارات

التفاصيل في [دليل البناء](docs/BUILDING.md) و[نطاق الاختبارات](docs/TESTING.md).

```powershell
.\Build.ps1

dotnet run --project .\tests\Engine.Tests\Engine.Tests.csproj -c Release -- --report-dir .\_build\engine-tests
```

التطبيق مبني بـWPF وWebView2 وواجهة React/CSS مرفقة محليًا. صفحة التعريف الثابتة في `docs/site/`. لا تُرفع قواعد البيانات أو السجلات المحلية أو مخرجات البناء أو الأدلة الهندسية الخاصة إلى Git.

## الترخيص

المصدر بترخيص [MIT](LICENSE). تبقى المكونات الخارجية وشعارات المتصفحات تحت شروط أصحابها؛ راجع [الإشعارات](THIRD_PARTY_NOTICES.md). احذف البيانات الشخصية من السجلات والصور قبل فتح بلاغ عام.
