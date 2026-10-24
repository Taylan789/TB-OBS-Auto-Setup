# TB OBS V35

TB OBS artık Ollama'yı kullanıcıya teknik bir kurulum olarak göstermeden hazırlar.

## İlk açılış
1. TB OBS açılır.
2. Ollama bilgisayarda aranır.
3. Bulunamazsa resmi OllamaSetup.exe otomatik indirilir.
4. Kurulum sessiz modda yapılır.
5. Ollama arka planda başlatılır.
6. `qwen3:4b` modeli kontrol edilir ve yoksa otomatik indirilir.
7. TB AI hazır olduğunda kullanıcı normal Türkçe komut yazabilir.

Ollama Windows kurulumu kullanıcı hesabına yapılabildiği ve resmi kurulumcu sessiz kurulum parametrelerini desteklediği için bu yöntem kullanılır. İlk model indirmesinde internet bağlantısı ve yeterli disk alanı gerekir.
