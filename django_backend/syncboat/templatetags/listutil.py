from django import template
import math
register = template.Library()
def split(a, n):
        k, m = divmod(len(a), n)
        return (a[i * k + min(i, m):(i + 1) * k + min(i + 1, m)] for i in list(range(n)))
@register.filter
def partition(thelist, n):
    
    # """
    # Break a list into ``n`` pieces. The last list may be larger than the rest if
    # the list doesn't break cleanly. That is::

    #     >>> l = range(10)

    #     >>> partition(l, 2)
    #     [[0, 1, 2, 3, 4], [5, 6, 7, 8, 9]]

    #     >>> partition(l, 3)
    #     [[0, 1, 2], [3, 4, 5], [6, 7, 8, 9]]

    #     >>> partition(l, 4)
    #     [[0, 1], [2, 3], [4, 5], [6, 7, 8, 9]]

    #     >>> partition(l, 5)
    #     [[0, 1], [2, 3], [4, 5], [6, 7], [8, 9]]

    # """
    try:
        n = int(n)
        thelist = list(thelist)
        print(thelist)
        p = len(thelist)+1
        d = math.ceil(p/n)
        outob = list(split(thelist, d)) 
        print(outob)
        return outob
    except (ValueError, TypeError):
        
        return thelist
    

@register.filter
def partition_horizontal(thelist, n):
    # """
    # Break a list into ``n`` peices, but "horizontally." That is, 
    # ``partition_horizontal(range(10), 3)`` gives::
    
    #     [[1, 2, 3],
    #      [4, 5, 6],
    #      [7, 8, 9],
    #      [10]]
        
    # Clear as mud?
    # """
    try:
        n = int(n)
        thelist = list(thelist)
    except (ValueError, TypeError):
        return [thelist]
    newlists = [list() for i in range(n)]
    for i, val in enumerate(thelist):
        newlists[i%n].append(val)
    return newlists

@register.filter
def removeUnderscore(thetext):
    thetext = str(thetext).replace("_"," ")
    return thetext

@register.filter
def spaceToUnderscore(thetext):
    thetext = str(thetext).replace(" ","_")
    return thetext


@register.filter
def flattenqset(qset):
    outob = []
    for i in qset:
        outob.append(str(i))
    return outob

@register.filter
def replace(thetext, string):
    string = string.split("\\|")
    thetext = str(thetext).replace(string[0],string[1])
    return thetext

@register.filter(name='has_group') 
def has_group(user, group_name):
    return user.groups.filter(name=group_name).exists() 

@register.filter
def multiply(value, arg):
    return value * arg

@register.filter
def get_class(value):
    return value.__class__.__name__



@register.filter
def convertIntToExcelCol(n, extra=0):
    import string
    b=string.ascii_uppercase
    n = int(n)+int(extra)
    d, m = divmod(n,len(b))
    return convertIntToExcelCol(d-1, 0)+b[m] if d else b[m]