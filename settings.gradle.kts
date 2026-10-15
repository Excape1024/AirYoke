// 仓库统一走阿里云镜像：Maven Central 与 Google Maven 在本机实测会卡死
// （拉取 300MB 后停滞，2 分钟内零写入）。镜像放最前，官方源兜底。
//
// 若换了网络环境出现依赖解析失败，把下面的 aliyun 几行删掉即可回到官方源。

pluginManagement {
    repositories {
        maven("https://maven.aliyun.com/repository/gradle-plugin")
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/public")
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/public")
        google()
        mavenCentral()
    }
}

rootProject.name = "AirYoke"
include(":app")
