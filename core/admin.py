from django.contrib import admin

from core.models import Element


class ElementAdmin(admin.ModelAdmin):
    pass


admin.site.register(Element, ElementAdmin)
