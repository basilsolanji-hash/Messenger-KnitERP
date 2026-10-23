package ru.kniterp.messenger

import android.content.res.Configuration
import android.os.LocaleList
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import java.util.Locale

@RunWith(AndroidJUnit4::class)
class LanguageResourcesTest {
    @Test fun fourLocalesResolveWithoutRussianFallback() {
        val base = InstrumentationRegistry.getInstrumentation().targetContext
        for ((tag, expected) in mapOf("ru" to "Настройки", "be" to "Налады", "kk" to "Баптаулар", "uz-Latn" to "Sozlamalar")) {
            val config = Configuration(base.resources.configuration)
            config.setLocales(LocaleList(Locale.forLanguageTag(tag)))
            assertEquals(expected, base.createConfigurationContext(config).getString(R.string.settings))
        }
    }

    @Test fun registrationActionsHaveFourTranslations() {
        val base = InstrumentationRegistry.getInstrumentation().targetContext
        for ((tag, expected) in mapOf("ru" to "Завершить подключение", "be" to "Завяршыць падключэнне",
            "kk" to "Қосылуды аяқтау", "uz-Latn" to "Ulanishni yakunlash")) {
            val config = Configuration(base.resources.configuration)
            config.setLocales(LocaleList(Locale.forLanguageTag(tag)))
            assertEquals(expected, base.createConfigurationContext(config).getString(R.string.auth_retry_activation))
        }
    }
}
