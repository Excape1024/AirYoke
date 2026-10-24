plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.airyoke"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.airyoke"
        minSdk = 33
        targetSdk = 35
        versionCode = 16
        versionName = "1.5.1"
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
        release {
            // R8 混淆 + 资源压缩：能把 Material/AndroidX 里没用到的部分剥掉。
            // 这个 App 不用反射、没有动态加载，混淆是安全的。
            isMinifyEnabled = true
            isShrinkResources = true
            // 先用 debug 签名，保证 release APK 也能直接侧载安装（阶段 5 再换正式 keystore）
            signingConfig = signingConfigs.getByName("debug")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        viewBinding = true
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    // material 只为 Theme.Material3.* 主题；界面全部是自绘 View + 原生控件，
    // 所以 R8 能把 Material 里绝大部分剥掉。
    implementation("com.google.android.material:material:1.12.0")

    testImplementation("junit:junit:4.13.2")
}
